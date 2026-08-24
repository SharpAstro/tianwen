# Plate-solver performance: closing the gap to ASTAP

Status: PLANNED. The correctness work is done and shipped; everything below is performance only,
and must not cost a single solve.

## Where this came from

A 135-frame 10P/Tempel stack (QHY294C, SV 545 f/4.5, 5.4 deg field at 4.72"/px) exposed a real
solver bug: a seeded solve re-opened the blind match tolerance, so `CatalogPlateSolver` failed on
4 of 10 frames that ASTAP solves 10 of 10. Fixed. While measuring that fix the timings turned out
to deserve their own plan.

## The measured budget

Whole-process wall clock, AOT-published `tianwen solve`, frame 0018, fresh file per rep, median of
4. Stage numbers come from `PlateSolveFailureProbe` (`TIANWEN_STAR_PROBE_FITS`).

| | wall (median) |
|---|---|
| ASTAP `astap_cli`, 5 deg hint | **162 ms** |
| `tianwen` AOT, same 5 deg hint | **1158 ms** |
| `tianwen` AOT, blind | 1203 ms |
| `tianwen` via `dotnet run`, blind | 2412 ms |

Read two things off that before optimising anything. **AOT is half the story**: 2412 -> 1203 ms is
pure managed startup, and the shipped artifact is the AOT one, so 1.2 s is the honest user-facing
number. And **a hint buys us only 45 ms**, so the remaining 7.1x is structural, not an artifact of
how the two were invoked. (An earlier comparison was invalid because ASTAP was hinted and ours was
not; that is why the hinted row exists at all.)

The 1158 ms decomposes:

| stage | time | share |
|---|---|---|
| fixed init (Tycho-2 bulk decode dominant) | ~590 ms | 51% |
| matching iterations | 446 ms | 39% |
| star detection (1600 stars, 4164x2795) | 83 ms | 7% |
| catalog query (2694 stars, R=4.04 deg) | 54 ms | 5% |

## What ASTAP does differently

From `C:/temp/astap/command-line_version/unit_command_line_solving.pas`. Four decisions, each
mapping onto one phase below.

1. **Bin before detecting.** `report_binning` returns 2 when image height exceeds 2500 px. Our
   frames are 2795 high, so ASTAP detects on 2082x1397: a quarter of the pixels.
2. **Cap the star list hard.** `get_brightest_stars(max_stars {500}, ...)`. It solved this field
   from 527 stars where we carry 1600 into matching.
3. **Match scale-invariant quad DESCRIPTORS, not positions.** `find_quads` stores six values per
   quad: index 0 the longest side, indices 1..5 the other five distances *scaled to it*.
   `find_fit` is a double loop over ~409 image quads x ~275 database quads of five cheap `abs`
   compares with early-out: about 110k comparisons, once.
4. **Derive scale from the match.** The median longest-side ratio over matching quads IS the plate
   scale, with outlier rejection on that ratio. The header focal length is never trusted.

Ours runs a pair-RANSAC seed (up to 916k hypotheses on the parity that does not lock) and then an
O(n^2) proximity loop of 1600 detected x ~2000 projected, ~3.2M distance computations **per
iteration, times four iterations, times two parities**. That is the gap: ASTAP does ~110k cheap
comparisons once; we do millions, repeatedly.

**We already own a quad matcher.** `FrameRegistration.TryMatchAsync` and
`SortedStarList.FindQuadsAsync` do exactly this for frame-to-frame stacking registration (the
`--quad-stars` knob). It has never been pointed at the catalog side.

## Phases

Ordered by return per unit of risk; each independently shippable and measurable.

| # | change | expected | risk |
|---|---|---|---|
| A | Short-circuit the losing parity | -200 ms | low |
| B | Tycho-2 pre-baked region index (TODO 2C) | -500 ms | low |
| C | Quad-descriptor matching against the catalog | -300 ms | medium |
| D | Bin before detection, cap the star list | -60 ms | low |

Target after all four: **~200 ms**, against ASTAP's 162 ms.

### A. Short-circuit the losing parity

Both parities run as `Task.Run` siblings and the loser is pure waste: on frame 0018 the correct
parity locked after **32,179 hypotheses** while the wrong one ground through **915,994**, twice
(anchor margin 0% and 10%). Cancel the sibling once one seeds with consensus far above chance.
This is the only phase that helps the FIRST solve of a session, which is why it leads.

Parity can also be carried rather than rediscovered, and `WCS.HasCDMatrix` plus
`sign(CD1_1*CD2_2 - CD1_2*CD2_1)` IS the parity. `searchOrigin` is already a parameter on
`SolveFileAsync` / `SolveImageAsync`, and today a hint is used for POSITION only while its
orientation is discarded. A header hint carries no CD matrix (the probe prints `hasCD=False`), so
this pays off only where a caller feeds back a previous solve, which `IncrementalSolver` and the
polar-align loop already do.

**Cache it per CAMERA, never per rig or per OTA.** Parity is set by the number of reflections in
that camera's own light path, and an off-axis guider inserts one: its pick-off prism is a single
reflection before the imaging plane, so on a refractor + OAG the main camera is even-parity and the
guide camera is ODD -- opposite parity, same rig, at the same time. This is not hypothetical here:
the polar-align loop solves GUIDER frames while the session solves IMAGING frames, so one cached
parity per rig would be actively wrong for one of them. Key it by camera device id, the way
per-focuser backlash is keyed in `Profiles/BacklashHistory/`.

**Do not try to infer parity from the declared optical train.** Counting reflections is
insufficient even in principle, because FITS ROW ORDER contributes: a `BOTTOM-UP` frame read as
`TOP-DOWN` is a vertical flip, i.e. a parity change with no mirror anywhere in the optics.
Conventions differ across capture software. Learning parity from the first successful solve
captures optics and convention together, needs no user declaration, and is self-correcting.

**Parity stays a hint, never a constraint.** A stale cached parity (someone added a diagonal, moved
the camera to the OAG port, changed capture software) must not turn a solvable frame into a
permanent failure. Skip the other branch speculatively; fall back to trying both on failure. That
costs nothing in the common case and stays correct in the rare one.

### B. Tycho-2 pre-baked region index

Half the wall clock is init, and it is one known item: `TODO.md` "Catalog cold-start Phase 2", where
2A (`hd_hip_cross.bin.gz`) and 2B (`simbad_merge.bin.gz`) shipped and **2C, the Tycho-2 bulk load,
is deferred**. ASTAP never pays this: its `.1476` files are indexed by sky region and it reads only
the window it needs (342 database stars for this field). This measurement is the argument for doing
2C -- largest single line item, already scoped.

### C. Quad-descriptor matching against the catalog

The substantive one. Build quads from the top-K detected stars (existing machinery), build quads
from the catalog window, match on the five scaled distances, take the plate scale from the median
longest-side ratio. This replaces the iterative proximity loop and also removes our dependence on
the header scale -- right independently of speed, since `FOCALLEN` was 1.2% wrong on this rig
(205 mm nominal against a solved 202.4 mm) and is only ever a hint.

Keep the pair-RANSAC seed: it is what makes dense fields work (see the Vela notes) and it is cheap
once phase A stops paying for its losing parity. The quad path replaces the REFINEMENT loop, not
the seed.

### D. Bin before detection, cap the star list

Follow `report_binning`: bin 2x above ~2500 px height, and cap the list carried into matching at
~500 brightest. A `detectionScale` downsample path already exists but is gated on a target pixel
scale, and at 4.66"/px it does not trigger -- a height-based rule is the missing half. Smallest of
the four, listed because it is nearly free.

## Correctness gates

Speed that costs accuracy is a regression, so every phase is measured against the same oracles.

1. **`VelaMosaicFieldTests`** -- 24 real pointings / 96 frames / 78k catalog stars with
   gate-verified WCS as ground truth, including 106 overlapping and 272 disjoint panel pairs. The
   disjoint pairs are the negative case: none may start locking.
2. **This comet field** -- 10 of 10 frames, plate scale within 4.7160-4.7180 (ASTAP: 4.7172 +/-
   0.0013), SIP rms 0.11 px at order 3, and the 135-frame master solving blind at 457/1680.
3. **The acceptance gate stays exactly as strict.** It behaved correctly throughout: it refused a
   27.75 px fit rather than emit a wrong WCS. Nothing here may loosen `GateTolerancePx` or the
   chance threshold to make a number look better.

Add `PlateSolveBenchmarks` to `TianWen.UI.Benchmarks` so these figures stop being hand-timed wall
clock. Everything above was measured by hand: fine for deciding what to do, not fine for detecting
a regression.

## Explicitly not in scope

Matching 162 ms exactly. ASTAP is a mature dedicated solver with its own on-disk catalog format;
the goal is to stop being 7x slower, not to win. Phases A and B alone reach roughly 460 ms with no
algorithmic risk, which is already the difference between "noticeable" and "unnoticed" in a session
that solves once per target.
