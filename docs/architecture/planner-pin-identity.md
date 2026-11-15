# Planner pin identity

What `CLAUDE.md` states in one line, in full.

## Moved from CLAUDE.md, 2026-11-15

The text `CLAUDE.md` carried until 2026-11-15, where this document did not already hold it; `CLAUDE.md` keeps one line
per rule and points here.

### Planner Pin Identity (a pinned planet is not its position) (from CLAUDE.md)

**A proposal must always produce exactly one row in the planner list, and object identity for a
solar-system body is its `CatalogIndex` -- never the `Target` value.** Both halves are load-bearing; the
bug they fix was "Venus is in a proposal but doesn't appear in the planner so I can't remove it".

- **`Target` is a positional record**, so its equality includes RA/Dec -- and for a planet, the Moon, or
  a comet those are *ephemeris values resolved at an instant*, not identity. Venus pinned at last
  night's `AstroDark` is a different `Target` value from tonight's Venus. Match through
  **`PlannerActions.IsSameObject`** (index-equality gated on `CatalogIndex.IsSolarSystemObject`, exact
  record equality otherwise), used by `FindProposalIndex` (the removal path), the proposal->score
  resolve, and `AddProposal`'s duplicate check. Do **not** widen it to all catalogued objects: mosaic
  panels share an index and differ only by their offset centre.
- **`GetFilteredTargets` never drops a proposal.** The pinned section is a *projection* of `Proposals`
  (so `PinnedCount == Proposals.Length`, which is also what the N-1 `HandoffSliders` indexing assumes),
  not a filtered subset of it. `ResolveProposalScore` prefers tonight's list -> search results -> the
  score cache and, failing all three, **synthesizes** a row from the proposal itself. This is what makes
  the failure class impossible rather than merely unlikely: the row IS the unpin affordance (the `[-]`
  button and the keyboard toggle both act on it), so a proposal that resolves to nothing and is dropped
  is a pin the user can neither see nor remove, while it keeps being re-saved and re-scheduled.
- **Two independent ways in, both now closed.** `ComputeTonightsBestAsync` rebuilds `ScoredTargets`
  from tonight's list alone and **the scheduler never sweeps planets** (they arrive only via search /
  `CommitSuggestion` / a sky-map pin), so any full recompute orphaned a pinned planet. And
  **solar-system bodies are stored in the object DB with `double.NaN` coordinates**, so
  `PlannerPersistence.MatchTarget`'s DB fallback rebuilt a restored pin at NaN/NaN; it prefers the saved
  proposal's own RA/Dec whenever the catalog's is not a number, and a comet -- never in the DB by design
  -- restores from the proposal directly instead of being discarded.

Pinned by `PlannerSolarSystemPinTests`.
