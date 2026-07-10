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
| **C-MCP — agent lookup** | The `tianwen-mcp` `catalog.lookup` tool is comet-aware: a comet designation (numbered `10P`, `12P/Pons-Brooks`, provisional `C/2023 A3`) resolves to a LIVE two-body ephemeris (J2000 RA/Dec, predicted vmag, r/delta, perihelion date + brightening/fading trend) instead of `NOT FOUND`/a static row. Supplying `latitude`+`longitude` adds observability: a comet gets tonight's dark window + peak altitude and a weekly outlook with the recommended BEST night over ~6 months (brightest night clearing the horizon floor while dark -- the brightness-vs-altitude tradeoff), in the site's LOCAL timezone; a fixed object gets rise/transit/set. Backed by the pure, tested `CometObservability` (Lib) over `CometEphemeris` + `ObservationScheduler.CalculateNightWindow`; `AddExternal()` added to the MCP host for the comet cache's `IExternal`/`ITimeProvider`; MCP globalization enabled so IANA timezones resolve. Also fixed `Transform.TryGetSiteTimeZone` to not throw on an unresolvable tz (it now falls back cleanly). **Smoke-validated over stdio JSON-RPC** (12P from Sydney across its 2024 apparition; 10P/Tempel; M31 fixed-object). | **DONE** |
| **C2a — sky-map F3 search** | Comets are searchable in the sky-map F3 modal by designation AND common name, resolving + committing (slew + info panel + sparkline) through the comet repository -- WITHOUT touching the `CelestialObjectDB` immutability invariant. `SkyMapSearchActions.OpenSearch` merges comet keys into the search index + a `SkyMapSearchState.CometEntries` name->index map; `FilterResults` resolves a comet-only string via that map (a real catalog object still wins a name tie); `CommitResult` has a comet branch (live `TryGetPosition` -> `CometInfoPanel`). **Inspector-validated: searching "Pons-Brooks" commits to 12P (Scorpius) with the comet info panel + vmag sparkline.** | **DONE** |
| **C2b-scoring — planner proposals + boost** | `ObservationScheduler.TonightsBest` now sweeps the comet repository after the DB/circumpolar sweeps: a cheap static candidacy gate (peak-ish `M1 + K1*log10(q)`) skips comets that could never reach the floor, survivors are resolved at astronomical midnight (`ICometRepository.TryGetPosition`), and scored via the existing `ScoreTarget` with `ObjectType.Comet` + a **vmag-driven `objectBonus`** (`(CometPlannerMagnitudeFloor 15 - m) * CometBonusScale 30`, so a naked-eye comet lands ~300 -- comparable to a top named DSO -- and a mag-15 comet drops off). Threaded through `PlannerActions.ComputeTonightsBestAsync` (+ `EnsureLoadedAsync`) and both GUI callers + the CLI `tianwen plan`. **Inspector-validated: 10P/Tempel appears in Tonight's Best (type Com, 68deg, rating 4.7 stars) with its altitude profile on the schedule chart, selectable/pinnable like any DSO.** Pinned comets carry their plan-time position (plate-solve centering handles the intra-plan drift; nightly re-resolution deferred). | **DONE** |
| **C2c — planner-tab search + vmag chart** | The planner TAB's own search/autocomplete (`SetAutoCompleteCache`, a separate `CreateAutoCompleteList` copy from the sky map) does not yet include comets; a dedicated `MagnitudeChartRenderer` (mirror `AltitudeChartRenderer`, inverted Y) + `PlannerState` vmag profiles for a selected comet is still open. The `ISolarSystemEphemeris` resolver service was NOT needed for any shipped path (sky map, planner, MCP all use `ICometRepository` directly); revisit only if a Session-loop / non-sidereal path needs unified planet+comet resolution. | **NOT STARTED** |
| **D — object paths on selection** | On selecting a solar-system object (planet OR comet), draw its motion across the sky as a thin polyline under the reticle. `SkyMapState.GetSelectedPathCached` samples `SkyPathSampleCount=49` J2000 positions over a body-appropriate window (Moon 5 d, comet 45 d, planet 120 d) -- planets via `VSOP87a.ReduceJ2000`, comets via `CometEphemeris` -- cached by `(index, day)` like the vmag sparkline; `SkyMapTab.DrawSelectedObjectPath` projects + polylines it with the wrap-guard, in the object's colour. Cache hits on `(index, day)` regardless of sample count (an empty result must still cache, else it re-samples every frame -- fixed). **Inspector-validated: Mars draws its ecliptic-hugging arc; 12P/Pons-Brooks draws a distinct cyan arc through the reticle, clearly its own motion (not the ecliptic), under the reticle in comet-cyan.** Render-thread sampling cost is tracked for an off-thread move (task #26). | **DONE** |
| **E — docs + memory** | This plan, `summary.md` row, `CLAUDE.md` section, project memory. | **IN PROGRESS** |

## Deferred (Phase 4 / later)

- **Per-object Horizons ephemeris** for a *pinned* comet over the plan window (sub-arcsec vs the
  arcminute two-body approximation) — mainly buys accurate **non-sidereal tracking rates**
  (`IMountDriver.SetRightAscensionRateAsync` / `SetDeclinationRateAsync` exist but nothing calls them
  yet). Two-body + plate-solve centering is sufficient for GoTo without it.
- **Intra-night non-sidereal tracking** during long exposures (the Session currently treats a target as
  a fixed RA/Dec captured at plan time; nightly re-resolution needs no Session change, per-frame does).
- **Bright asteroids** via the identical pipeline (`sb-kind=a` + an H/G magnitude law + `Catalog.MinorPlanet`).
- **Off-thread sky-map ephemeris sampling** (task #26, **required** not optional): the selected-object path
  (`GetSelectedPathCached`) and the vmag sparkline (`GetCometMagnitudeCurveCached`) sample ~49 + ~32
  ephemerides on the render thread on a cache miss. One-shot per selection is fine, but held day-scrubbing
  re-samples (~80 evals, ~15 ms) per keypress on the render thread. Move both behind the codebase's
  lock-free `Task<T>` hand-off (mirror the Milky Way / Tycho-2 async swaps): compute off-thread, poll on the
  render thread, draw the previous samples until the new ones arrive.
