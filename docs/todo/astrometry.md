# TODO -- Astrometry

Part of the TianWen TODO set. See [TODO.md](../../TODO.md) for the index and the active/high-priority list.

## Astrometry / Catalogs

- [x] Update lib to accept spans in `CatalogUtils` (`CatalogUtils.cs:326,360`)
- [ ] Better Tycho VT->V transform (Bessell 2000) for the moderately-red population. Today `CelestialObjectDB.cs` uses the ESA *linear* relation `V = VT - 0.090(BT-VT)`, `B-V = 0.850(BT-VT)`; duplicated in the single-star decode (`TryGetTycho2StarByTycId`) and the bulk render loop (`CopyTycho2Stars`). Per the ESA Tycho Catalogue **Vol 1 §2.2** (formulas 2.2.1/2.2.2) this is valid only for `-0.2 < (BT-VT) < 1.8` **and only for unreddened main-sequence stars**. The same doc's Field T5 note is the stronger caveat: the catalog's own V (derived via the fuller transform in **§1.3 Appendix 4**) has *"much larger systematic errors ... especially for red stars, i.e. with B-V > 1.5 mag."* Antares is `B-V = 1.84`, so per ESA itself, **no Tycho VT->V transform reliably yields its V**, independent of the colour-range bound. Bessell (2000, PASP 112, 961) is a better fit but is a cubic-spline **lookup table defined only to `(BT-VT) = 2.0`**, and Antares (`BT-VT ≈ 2.20`) / R Leporis (`≈ 5.80`) are beyond even that. That's why `PreferCrossRefMagnitude` (commit aad748e) defers bright stars to a curated SIMBAD/HR V, and that backstop must stay regardless of which transform we use. Adopting Bessell would still help the `BT-VT ≈ 1.5–2.0`, `B-V < 1.5` population (mostly the rendered Tycho buffer): (1) source the exact table accurately (paper / AstroCalc source / §1.3 App.4, do **not** guess coefficients), (2) unify the two transform sites into one helper, (3) re-baseline every Tycho-magnitude test incl. R Lep's pinned `8.28` (extrapolated). Refs: ESA Tycho Cat Vol 1 §2.2 + §1.3 App.4 (local: `OneDrive/Dokumente/Astro-Info/TYC_Photometry_sect2_02.pdf`), Bessell 2000 (`iopscience.iop.org/article/10.1086/316598`), projectpluto.com/photomet.htm.

- [ ] (LOW PRIORITY) Data-encode the VSOP87 ephemeris coefficients. `VSOP87/*X.cs`/`*Y.cs`/`*Z.cs` are ~24 giant `GetX/GetY/GetZ` methods of thousands of inline `x += c*Math.Cos(p + f*t)` statements (~3.6 MB source). Re-encode as `static readonly double[]` (or a packed binary) + one generic eval loop: kills the dev-only ~330 ms first-call JIT (467 ms dev vs 7 ms AOT), shrinks the AOT binary, speeds the AOT publish. Same coefficients = full accuracy, so the `Transform.cs` GOTO/pointing consumers stay correct. NOT a production-perf fix. Full context + pipeline-prewarm sibling item in `docs/todo/ui.md` "Sky Map (first-open perf)" and the `reference_skymap_first_open_perf` memory.

## Astrometry / Plate Solving

- [ ] Extract distortion model (SIP polynomial coefficients) from plate solver output
- [ ] Implement image undistortion using extracted distortion model
- [x] `CatalogPlateSolver` can't solve drizzle outputs from the CLI (`tianwen solve <fits>`) -- root cause was **`ICelestialObjectDB.InitDBAsync` was never called from the CLI's solve path**. The `StackingPipeline` path works because `MasterPostProcessor.cs:114` explicitly awaits `InitDBAsync(waitForTycho2BulkLoad: true, ct)` before invoking the solver; the CLI's `solve` subcommand skipped it. Without init, the catalog query returned 0 stars and the solver bailed in ~50 ms with no useful diagnostic (the ctor accepted `ILogger? logger = null` and DI's non-generic `ILogger` resolution silently left it `null`, so internal `_logger?.LogDebug` lines never fired). Fix: (1) self-init inside `CatalogPlateSolver.SolveImageAsync` via the idempotent `_isInitialized` fast path so any caller works; (2) DI registration switched to a factory lambda in `AstrometryServiceCollectionExtensions.cs` that resolves `ILogger<CatalogPlateSolver>` and upcasts to the ctor's non-generic `ILogger`. Verified: SoL drizzle + drizzle_autocrop both solve cleanly via CLI (RA=11.196h Dec=-61.35°, 887/969 and 663/753 stars matched; ~580 ms cold including Tycho-2 bulk decode, ~70 ms warm).
- [ ] `IncrementalSolver` polar-align fast path is *slower* than the full solve (~1.2 s vs ~0.85 s) -- `FindOffsetAndRotationWithRetryAsync` starts the quad-tolerance sweep at 0.0001 and burns ~50 `FindFit` iterations before reaching the converging range (~0.005-0.05). Fixes: bias the start tolerance higher (~0.005) for the polar-align caller, and/or cache the previous frame's resolved tolerance and start each refine at `prev x 0.5`. Perf-only; correctness + gauge stability already fixed (see `docs/known-limitations.md` "Near-pole plate-solve").
- [ ] Rewrite the skipped `IncrementalSolverTests` for the quad-matching contract -- the old tests targeted the retired ROI-centroid path (`[Fact(Skip = ...)]`); the solver now quad-matches against a frozen seed via `StarReferenceTable.FindFit`.

## Astrometry / Comets (reported 2026-08-06)

- [x] **The faint magnitude near perihelion was NOT a bug, and chasing it found a real one.** Reported
  as "mag 12.75 looks wrong for 10P as it is near perihelion and near earth right now". Checked
  against the JPL Horizons API rather than reasoned about, and Horizons answers **T-mag 12.776** for
  the same instant, from the same M1 = 13.7 / K1 = 6.5, on solution JPL#K265/43 (soln.date
  2026-Jul-28, 6,347 observations through 2026). Our 12.75 is right. A comet can simply be that faint
  near perihelion. Pinned by `CometElementStalenessTests` so nobody edits the law.
  - **Two plausible causes were wrong and are recorded so they are not retried.** We are not reading
    the NUCLEAR parameters: SBDB and Horizons both report M2/K2 as n.a. for 10P. And the element
    record is not a decade-old FIT: its EPOCH field is 2016 but that is the osculating reference
    epoch, not the age of the solution, which is nine days old.
- [x] **The real defect: a comet marker can be 9.3 DEGREES out.** Found while checking the above, by
  comparing our propagator against Horizons for 10P at 2026-08-06 (a new case in
  `CometEphemerisTests`; the two existing ones are evaluated at their own element epoch, where
  two-body equals truth by definition, so nothing covered this).
  - **It decomposes exactly.** Our period, from the 2016 osculating `a` = 3.063862 AU, is 1958.82 d;
    JPL's for the current apparition is 1960.00 d. Propagating tp (2015-Nov-14) forward two
    revolutions lands perihelion at JD 2461258.38 against JPL's JD 2461254.62, i.e. **3.76 days
    late**. At perihelion 10P moves 31.0 km/s, so 3.76 days is 0.0674 AU of arc, and 0.0674 AU seen
    from delta = 0.4149 AU is 9.3 degrees.
  - So it is a pure TIMING error. The heliocentric and geocentric distances are right to a few parts
    in ten thousand: the comet is at the correct point of its orbit and the wrong point along it.
    Two-body propagation carries a fixed period while JPL fits non-gravitational terms (A1 = 2.5e-10,
    A2 = 8.1e-12 au/d^2) that shift the period by roughly a day per revolution for an active comet,
    and a period error integrates straight into phase. Worst exactly where it hurts: near perihelion,
    where the comet is both fast and close, which is when anyone would want to observe it.
  - **Mitigated, not fixed.** `CometElements.IsElementSetStale` reports an element set at least one
    revolution old, and the sky-map marker appends "?" to the NAME (never to the magnitude, which is
    correct). The error is pinned as an upper bound so it cannot silently grow.
- [x] **FIXED: the comet is fetched at its current apparition.** `HorizonsCometSource` asks
  `EPHEM_TYPE=ELEMENTS` with `COMMAND='DES=<desig>;CAP;'` for the osculating set at today's date, which
  is all `CometEphemeris` consumes, so the SAME propagator lands on Horizons: **0.35 arcseconds**
  against the 9.3 degrees from the 2016 record. Osculating elements at time T already carry the
  perturbation state at T, which is why this needs no non-gravitational force model.
  - **Per object, on demand, and only when it would help.** `ICometRepository.RequestCurrentApparition`
    is fire-and-forget and single-flight, called for a pinned comet and for one actually drawn on the
    map, and it returns immediately unless the bulk record is a revolution or more old. The SBDB bulk
    fetch stays the base layer: it is what makes 4,000 comets available offline in one keyless request.
  - **Overlaid, never replacing.** `TryGet` prefers the refined set, so the sky map, planner, search and
    MCP all improve with no per-caller wiring. Cached to `AppData/SmallBodies/apparitions.json` with a
    per-entry stamp (entries are fetched individually, so a shared stamp would make one fetch look like
    it refreshed the rest). Offline degrades to the bulk elements with the "?" still showing.
  - **Three traps worth keeping.** `OUT_UNITS='AU-D'` is load-bearing: the default is km, which parses
    as a valid double and would put the comet 150 million times too far away, so the parser has a range
    gate that rejects it. `CAP` picks the apparition in progress rather than leaving the request
    ambiguous. And the parse is all-or-nothing, because half a refined orbit is worse than an old but
    self-consistent one. Pinned against a FROZEN REAL RESPONSE in `HorizonsCometSourceTests`, since
    every failure mode here is a parsing one and a hand-written fixture would agree with a
    hand-written parser.
- [x] **Two different comets rendered with the same label.** SBDB's `name` is the DISCOVERER, so it is
  shared by construction: "Tempel" is eight comets, "SOHO" is 1,465, and 3,563 of 4,069 share a name
  with something. Searching "Tempel" listed a dozen indistinguishable rows in the planner, while the
  sky map showed exactly one (its 1:1 map kept the first and silently swallowed the rest). Suggestion
  lists now carry `CometElements.DisplayName` (one per comet, designation embedded),
  `CometSearchKeys.TryResolve` is two-pass so an unambiguous spelling always beats a bare shared name,
  and the sky map matches shared names through a dedicated bounded pass over comet aliases instead of
  the shared string index. Marker labels use the display name too.

## Astrometry / Catalogs (Queries)

- [ ] Check if SIMBAD supports angular size + dimensions in queries

