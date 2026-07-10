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
  Designations pack into a plain 7-bit-ASCII `CatalogIndex` (`c` tag + punctuation-free compact form,
  e.g. `cC2024A1`, `c73PC`) — **no MSB/Base91 bit-packing** (user constraint). `IsSolarSystemObject`
  now covers `Catalog.Comet` too, so the sky-map live-position path applies for free.
- **Coverage of the plain-ASCII packing:** 98% of the full SBDB catalog and **96% of *observable*
  comets** (q < 3 AU, with a magnitude model) pack within the 8-char payload budget. The ~4% that
  overflow are dual-designated active asteroids with asteroid-style two-letter half-month designations
  (e.g. `2001 OG108`), almost all faint; they are skipped and counted (never silently truncated).

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
| **A — identity + math core** | `Catalog.Comet` + `ObjectType.Comet`; `CometDesignation` parse/pack/canonical (plain-ASCII, prefix+pdes reconstruction, BC years, 2-letter half-months); `IsSolarSystemObject`; `CometElements`; `CometEphemeris` (universal-variable Kepler + M1/K1 vmag). Horizons-pinned tests. | **DONE** (commit `beab45ed`) |
| **B — data source + cache + registry** | `SbdbCometSource` (bulk fetch, `prefix`+`pdes` reconstruction, pure `Parse`, skip+count); `SbdbJsonContext` (source-gen, AOT); `CometRepository` cache `AppData/SmallBodies/comets.json` (weather-pattern TTL 7d + stale fallback, `FetchedUtc` envelope, `ITimeProvider`-driven); `CometDesignationJsonConverter`; `TryGetPosition`. DI in `AddAstrometry`. Live-validated: 3985 comets mapped. | **DONE (data layer)** |
| **C — search + sky map + planner + scoring + vmag UI** | Ingest comet indices + common names into `CelestialObjectDB` (F3 / autocomplete / `TonightsBest`); `Transform`/resolver position hook (comet branch alongside VSOP87a); sky-map dynamic comet markers (computed vmag < threshold) + info panel live position/vmag/**sparkline**; planner comet proposals (resolve at AstroDark); `CalculateObjectBonus` `ObjectType.Comet` boost driven by **computed current vmag**; `MagnitudeChartRenderer` (mirrors `AltitudeChartRenderer`, inverted Y) + `PlannerState` vmag profiles. | **NOT STARTED** |
| **D — docs + memory** | This plan, `summary.md` row, `CLAUDE.md` section, project memory. | **IN PROGRESS** |

## Deferred (Phase 4 / later)

- **Per-object Horizons ephemeris** for a *pinned* comet over the plan window (sub-arcsec vs the
  arcminute two-body approximation) — mainly buys accurate **non-sidereal tracking rates**
  (`IMountDriver.SetRightAscensionRateAsync` / `SetDeclinationRateAsync` exist but nothing calls them
  yet). Two-body + plate-solve centering is sufficient for GoTo without it.
- **Intra-night non-sidereal tracking** during long exposures (the Session currently treats a target as
  a fixed RA/Dec captured at plan time; nightly re-resolution needs no Session change, per-frame does).
- **Bright asteroids** via the identical pipeline (`sb-kind=a` + an H/G magnitude law + `Catalog.MinorPlanet`).
- **Asteroid-style two-letter designations** that overflow the plain-ASCII budget (would need a
  different packing; low value — faint dual-status objects).
