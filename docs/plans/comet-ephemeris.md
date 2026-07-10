# Comet Ephemeris & Small-Body Support

Add JPL comets as a **dynamic, ephemeris-computed catalog** — searchable (F3), plottable on the sky
map, scorable by the planner (with a brightness boost), and with realistic time-varying V-magnitude
curves — alongside the existing VSOP87 planets. Unlike a fixed star/DSO, a comet's RA/Dec **and**
brightness are functions of time, computed locally from cached orbital elements.

## Data model decision

- **Discovery source = JPL SBDB query API** (`ssd-api.jpl.nasa.gov/sbdb_query.api?sb-kind=c`), keyless
  HTTPS GET. SBDB is continuously updated from MPC observations, so **no separate MPC file** is needed.
  One bulk fetch (~4000 comets, designation + common name + M1/K1 + osculating elements) IS the
  "database". With the elements cached locally, **position and magnitude at any time are pure local
  computation** — the sky-map date slider, multi-night vmag curves, F3 search, and scoring all work
  offline with no per-object round-trip. (Per-object Horizons `OBSERVER` fetches are a deferred
  precision upgrade, see Phase 4.)
- **Identity = `Catalog.Comet`** (a dedicated catalog, not reused `Pl`) + `ObjectType.Comet`.
  `IsSolarSystemObject` now covers `Catalog.Comet` too, so the sky-map live-position path applies for
  free.
- **Packing = structured Base91 bit fields** (the same mechanism as `Tycho2`/`PSR`/`WDS`): 1-bit
  numbered/provisional discriminant + 3-bit kind + 11-bit fragment + either a 14-bit periodic number or
  (13-bit year + 10-bit half-month letters + 10-bit order) = ≤ 48 bits, Base91-encoded.
  A first cut used a readable plain-7-bit-ASCII index (`cC2024A1`), but the longest real designations
  (asteroid-style two-letter half-months of dual-designated active asteroids, e.g. `C/2001 OG108`,
  compact 10 chars) overflow the 9-char ASCII ceiling. The data settled it: **max compact length is
  exactly 10 chars and orders/years/numbers all sit in small ranges**, so structured bits reach
  **100% of the catalog** with no length ceiling. Trade vs. plain ASCII: the raw index is an opaque
  Base91 value, but `ToCanonical` still round-trips to `C/2024 A1` (what surfaces in search/display).

## Magnitude & position math

- **Position:** universal-variable (Stumpff-function) two-body Kepler propagation from the perihelion
  state, so one code path covers elliptic / parabolic / hyperbolic orbits (most observable comets sit at
  e ≈ 1). Earth from `VSOP87a`, light-time corrected, rotated to J2000 equatorial with the same matrix
  the planet path uses. Arcminute-class near the element epoch. **Does not** model non-gravitational
  (outgassing) forces or planetary perturbations.
- **Magnitude:** the IAU total-magnitude law `m = M1 + 5·log₁₀(Δ) + K1·log₁₀(r)` — exactly what
  Horizons' own T-mag column uses, so the curves are "realistic(ish)" by construction.
- **Validation:** pinned against JPL Horizons geocentric astrometric positions — 12P/Pons-Brooks
  (elliptic) and C/2023 A3 Tsuchinshan-ATLAS (near-parabolic) reproduce RA/Dec within 1 arcmin at their
  element epochs, and 12P's predicted T-mag matches Horizons' 15.037.

## Phasing

| Phase | Scope | Status |
|-------|-------|--------|
| **A — identity + math core** | `Catalog.Comet` + `ObjectType.Comet`; `CometDesignation` parse/pack/canonical (structured Base91 bit fields, prefix+pdes reconstruction, BC years, 2-letter half-months, 100% coverage); `IsSolarSystemObject`; `CometElements`; `CometEphemeris` (universal-variable Kepler + M1/K1 vmag). Horizons-pinned tests. | **DONE** (commit `beab45ed` + packing follow-up) |
| **B — data source + cache + registry** | `SbdbCometSource` (bulk fetch, `prefix`+`pdes` reconstruction, pure `Parse`, skip+count); `SbdbJsonContext` (source-gen, AOT); `CometRepository` cache `AppData/SmallBodies/comets.json` (weather-pattern TTL 7d + stale fallback, `FetchedUtc` envelope, `ITimeProvider`-driven); `CometDesignationJsonConverter`; `TryGetPosition`. DI in `AddAstrometry`. Live-validated: **4068/4068 comets mapped**. | **DONE (data layer)** |
| **C1 — sky map** | Sky-map dynamic comet markers (ephemeris-computed from the cached `ICometRepository`, filtered to a zoom-aware magnitude limit `max(CometBaseMagnitudeLimit=12, EffectiveMagnitudeLimit)`) drawn as a cyan coma dot + ring + anti-solar tail + name/mag label (the comet twin of `DrawPlanetLabels`); `com[e]t` (E key) toggle (`ShowComets`); clickable label → info panel with LIVE position/mag/alt-az/rise-transit-set (re-resolved per frame like a planet, since a comet moves AND brightens) + a **vmag sparkline** (state-cached `GetCometMagnitudeCurveCached`, +/-45-day window, brighter-up, "now" marker); click-select hit-test pass in `SkyMapSearchActions.SelectObjectByClick`. `PlannerState.Comets` wired + background `EnsureLoadedAsync` in `InitializePlannerAsync`. `CometEphemeris.SampleMagnitudeCurve` (pure, tested). **Live-validated via the SDL inspector: 5D/Brorsen renders at the correct ecliptic position (mag 11.4), the info panel + sparkline populate, and `[K]` hides/shows.** | **DONE** |
| **C2 — search + planner + scoring** | Ingest comet indices + common names into `CelestialObjectDB` (F3 / autocomplete / `TonightsBest`); `Transform`/resolver position hook (comet branch alongside VSOP87a — the chosen shape is an `ISolarSystemEphemeris` resolver service, NOT a repository injected into `Transform`); `SkyMapSearchActions.CommitResult` comet branch; planner comet proposals (resolve at AstroDark); `CalculateObjectBonus` `ObjectType.Comet` boost driven by **computed current vmag**; `MagnitudeChartRenderer` (mirrors `AltitudeChartRenderer`, inverted Y) + `PlannerState` vmag profiles. | **NOT STARTED** |
| **D — docs + memory** | This plan, `summary.md` row, `CLAUDE.md` section, project memory. | **IN PROGRESS** |

## Deferred (Phase 4 / later)

- **Per-object Horizons ephemeris** for a *pinned* comet over the plan window (sub-arcsec vs the
  arcminute two-body approximation) — mainly buys accurate **non-sidereal tracking rates**
  (`IMountDriver.SetRightAscensionRateAsync` / `SetDeclinationRateAsync` exist but nothing calls them
  yet). Two-body + plate-solve centering is sufficient for GoTo without it.
- **Intra-night non-sidereal tracking** during long exposures (the Session currently treats a target as
  a fixed RA/Dec captured at plan time; nightly re-resolution needs no Session change, per-frame does).
- **Bright asteroids** via the identical pipeline (`sb-kind=a` + an H/G magnitude law + `Catalog.MinorPlanet`).
