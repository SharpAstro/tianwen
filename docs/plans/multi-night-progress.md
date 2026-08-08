# Multi-Night Progress (accumulated integration per target, on the home screen)

**Status: NOT STARTED** (factored out of the multi-rig dashboard work 2026-07-29; idea: user,
2026-07-27, after the Vaonis smart-scope multi-night feature).

The home screen currently answers one axis: what is happening *now*, per rig. This plan adds the
other axis: what is accumulating *over time*, per target, "M31 · 4.2 h of 12 h · 3 nights", and
makes the scheduler aware of it, so an incomplete target carries over to the next session instead
of starting from zero in the planner's eyes.

The dashboard was deliberately built to admit this: the rig section is **content-sized** (a
`Grid(columns).WithAutoRows()` plus a trailing `Spacer`), never Star-filled, precisely so a second
section can join it without reworking the board or silently resizing every card
([remote-profile.md](remote-profile.md) § multi-rig dashboard, "leave room").

## The load-bearing decision: the ledger is a header scan, not new acquisition plumbing

Cumulative integration per target is **derived from the FITS files on disk**, not accumulated by
session counters:

- `StackingPipeline`'s scan already groups lights by FITS headers (`MasterGroupKey`, OBJECT /
  FILTER / EXPTIME). The ledger is a cheaper, headers-only pass over the same tree: no pixel
  decode, just header cards.
- Files are durable in a way counters are not: a crashed session, a deleted bad sub, frames
  captured by a different tool into the same tree, a re-installed OS; the ledger is correct
  after all of them because it *re-reads the truth* rather than trusting a running total.
- The provenance-skip rule applies verbatim: TianWen-produced masters/enhanced outputs
  (`STACK_N > 0` / `IntegrationFitsWriter.IsTianWenProduct`) are never counted as subs.

**Never** feed the ledger from `TotalFramesWritten` or any in-memory session state. The session
may *invalidate* the ledger's cache (a frame was just written), but the numbers come from headers.

## Phasing

| Phase | What | Status |
|-------|------|--------|
| P0 | **`IExposureLedger`** (pure Lib): headers-only sweep of the lights tree → per-target totals (`TargetKey` from OBJECT normalised via catalog lookup where possible; per-filter seconds; distinct nights via DATE-OBS local-midnight bucketing at the site). Incremental: per-directory mtime + file-count fingerprint, full rescan only on mismatch; cache under `AppData/Ledger/<profileId>.json` with the weather-pattern envelope (`FetchedUtc`, stale-tolerant). | NOT STARTED |
| P1 | **Target goals**: optional per-pin integration goal (hours, optionally per filter) on the planner pin, persisted with `PlannerPersistence` per profile; the "of 12 h" denominator. No goal → the line shows accumulation only ("4.2 h · 3 nights"). Goals live with pins, NOT in the profile (a goal is per-campaign, not per-rig, the no-varying-values-in-profile rule). | NOT STARTED |
| P2 | **Scheduler carry-over**: `ObservationScheduler` takes an optional ledger; scoring gets a completion term (unmet-goal targets score up, met-goal targets score to ~0 unless re-pinned), and `FilterPlan` building subtracts accumulated per-filter time from the goal split. Same pattern as the comet repository: an optional dependency, byte-identical schedule when absent. | NOT STARTED |
| P3 | **Home-board section**: an "Accumulating" list under the rig grid; one line per pinned target with a goal or with history (`M31 · ████░░ 4.2/12 h · 3 nights`), built in `HomeBoard`/`HomeBoardLayout` as `Layout.Node`s so **GUI and TUI get it from the same tree** (the board already proved that path). Read-only, zero device I/O, built pre-gate like the cards. Collapses below a height threshold rather than squeezing the rig cards. | NOT STARTED |
| P4 | **Remote rigs**: the ledger is node-scoped (the files live on the node), so remote cards need it over the wire; `GET /api/v1/ledger` returning a concrete `LedgerDto` (registered in `HostingJsonContext`; numeric enums; no `required` nullable members), cached per `RemoteRigConnection` on a slow cadence (minutes, it changes once per written frame at most). Board merges: local ledger + per-bound-rig ledgers, labelled by rig. | NOT STARTED |

## Invariants (set now, before code exists)

- **Headers are the source of truth.** See above; the one rule that keeps the number honest.
- **The board section inherits the dashboard's invariants**: read-only w.r.t. hardware, previews
  stay off, zero device I/O by construction (built pre-gate, not added to `PollPreviewTelemetry`'s
  `ActiveTab` gate), content-sized.
- **The scan never runs on the render thread.** P0's sweep is a background task publishing an
  immutable snapshot (`ImmutableArray<TargetLedgerEntry>` swapped by reference); the board reads
  the last published snapshot exactly like `GuiAppState.HomeCards`.
- **A night is a site-local bucket, not a UTC calendar day.** DATE-OBS bucketed by local
  astronomical midnight at the site; an 11 pm-to-3 am run is ONE night. Time zone from the
  profile site (the same `SiteLatitude/Longitude` → timezone path the planner uses).
- **Identity matches by catalog resolution, not string equality.** "M 31", "M31", "NGC 224" and
  "Andromeda Galaxy" are one target: normalise OBJECT through `CatalogIndex` where it resolves
  (the SIMBAD cross-index work makes this cheap); fall back to case/whitespace-folded string for
  unresolvable names. Never double-count a target because two tools spelled it differently.

## Open questions (decide at the phase, not now)

- **Cross-rig aggregation of one target** (two rigs imaging M31 on the same night): v1 displays
  per-node lines labelled by rig; summing across nodes needs a shared identity + dedup story and
  is deferred until someone actually images one target from two rigs.
- **Mosaic panels**: `MosaicGroupId` links panels; does a mosaic accumulate as one target or N
  panels? Lean N panels with a group rollup line, but decide when P0's key design is on the table.
- **Sub-quality weighting**: count wall-clock integration only, or discount frames the quality
  gate would reject? v1 counts wall-clock (matches what every other tool reports); revisit if the
  AI-dataset quality gate (`SessionFrameAnalyzer`) becomes a shared service.

## Related

- [remote-profile.md](remote-profile.md) § multi-rig dashboard; the design record this extends.
- [docs/todo/sequencing.md](../todo/sequencing.md) "Multi-night scheduling"; the originating
  backlog entry (now pointing here) and its sibling "Persistent observation database", which P0's
  ledger partially subsumes (proposals/history persistence stays separate).
