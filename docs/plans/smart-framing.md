# Smart Framing — co-framing groups in the planner

Pinning M8 with a wide-field profile should automatically image M20 in the same frame: the planner
derives the sensor FOV from the active profile and groups pinned targets with catalogued neighbours
that share a single pointing ("M8 + M20"), collapsing them to one scheduled observation at the
combined-footprint centroid.

## Status: Phases 1-3 SHIPPED, Phase 4 (sky-map group frame) DEFERRED

| Phase | What | Where | Status |
|-------|------|-------|--------|
| 1 | Pure grouping core: `FramingCandidate`/`FramingGroup`/`FramingOptions` + `FramingGrouper.Group` (tangent-plane fit, greedy nearest-accretion, RA-seam wrap) | `TianWen.Lib/Sequencing/FramingGroup.cs`, `FramingGrouper.cs` | DONE (`FramingGrouperTests`, 11 tests) |
| 2 | Sensor specs in the profile: `OTAData.CameraPixelSizeUm/CameraSensorWidthPx/CameraSensorHeightPx`, auto-captured on first camera connect (`EquipmentActions.CaptureSensorSpecs`, idempotent); offline FOV via `SensorFovExtensions` (`OTAData.SensorFovDeg`, `ProfileData.PrimarySensorFovDeg`) | `ProfileDto.cs`, `SensorFovExtensions.cs`, `AppSignalHandler` connect handler | DONE |
| 3 | Planner integration: `FramingPlanner.BuildGroups` (grid-local neighbour discovery) + `CollapseForSchedule` (one pointing per multi-target group); `PlannerState.SensorFovDeg`/`FramingGroups`; recompute hooked into `RecomputeHandoffSliders` + `BuildSchedule`; FOV refresh on planner init / recompute / sensor capture (`AppSignalHandler.RefreshSensorFovAndFraming`) | `FramingPlanner.cs`, `PlannerActions.ComputeFramingGroups`, `PlannerState` | DONE (`FramingPlannerTests` incl. real-catalog M8→M20 discovery) |
| 4 | Sky-map rendering: draw the shared FOV rectangle + member labels around each multi-target group | `VkSkyMapTab` | DEFERRED |

## Invariants

- **Not quadratic.** `FramingGrouper.Group` Dec-sorts once and binary-searches the
  `|dDec| <= fovHeight` band per seed with an O(1) RA pre-gate (`O(n log n + seeds*band*MaxMembers^2)`).
  Neighbour discovery is **grid-local** — only the `DeepSkyCoordinateGrid` cells covering each seed's
  FOV footprint are sampled, never a catalog scan — so `n` stays small by construction.
- **Identity is index-based.** Dedup goes through `ObservationScheduler.MarkCrossIndicesSeen`
  (cross-index table), the same mechanism as the planner sweep. No name comparison — alias
  completeness is the DB's job (see the v4 SIMBAD merge fix below). Discovered companions are limited
  to NAMED DSOs (`CommonNames.Count > 0`, non-star, non-candidate, non-duplicate) — notable enough to
  reframe for; explicitly pinned seeds group regardless.
- **Ungrouped path is byte-identical.** No FOV (sensor specs never captured) → `FramingGroups` empty →
  `CollapseForSchedule` is an identity transform. Zero regression risk for profiles without captured
  sensor geometry.
- **Sensor specs live in the profile JSON, not the camera URI** — the discovery reconcile can replace
  URIs; the specs survive. Captured once on connect, saved only on genuine change.
- **Seeds anchor groups; discovered neighbours never seed their own** (and are dropped if they fit no
  seed's frame). Co-framable seeds merge into one group. The collapsed observation keeps the first
  member proposal's imaging params (gain/offset/priority/window); only pointing + name become the
  group's.

## Catalog identity root-fix shipped with this feature (SIMBAD merge v4)

The M8 test exposed `Sh2-25` as a standalone "Lagoon Nebula" duplicate: SIMBAD ties Sh2-25 to the
Lagoon solely via the identifier `M 8` (it models NGC 6523 as a *contained* child, not an alias), and
Messier numbers exist in our DB only as cross-index aliases of their NGC entries — never as direct
`_objectsByIndex` entries — so `MergeSimbadRecords`' bare `TryLookupByIndexDirect` filter dropped
them and such records never cross-linked. Fix: `ResolveToDirectIndex` follows the cross-index table
(strictly widening the old acceptance), the `bestMatches` computation went LINQ-free (reused lists +
in-place sort, no per-record query/OrderBy/Distinct/ToList allocations, no enum-CompareTo boxing),
`AlgorithmVersion` 3→4, snapshots rebaked. Verified: `TryGetCrossIndices(NGC 6523)` now includes
`Sh2-25`; the Hyades name resolves to all three of its designations (C41/Mel25/Cr50, transitively
cross-linked); live-vs-snapshot equivalence + snapshot freshness pinned by `SimbadMergeSnapshotTests`;
init stays on the snapshot fast path (1.86 s total, 269 ms simbad phase; lookups ~120-190 ns).

Shipped alongside: full catalog data refresh (13 SIMBAD catalogs + OpenNGC), and the external `lzip`
binary fully retired — `tools/lzip-util.ps1` (managed SharpAstro.Lzip encoder AND decoder) is now the
single lzip path shared by `Get-SimbadCatalogs.ps1`, `Copy-OpenNGC.ps1`, and `preprocess-catalog.ps1`.

## Deferred

- Phase 4 sky-map group-frame rendering (shared FOV rectangle + member labels).
- Group-aware exposure planning (per-member filter weighting within one frame).
- A UI affordance to opt a specific pin out of grouping.
