# Plate-solver performance: closing the gap to ASTAP

Status: **PHASE A SHIPPED**; B-D planned. The correctness work was already done; everything below is
performance only.

## The governing rule

**Optimise only where it is provably free of accuracy cost. Where it is not, take the slower path
and say so.** A plate solve here is not a yes/no answer, it is a MEASUREMENT: the comet
ephemeris-to-pixel conversion, the polar-alignment error vector and mosaic panel placement all
consume the WCS quantitatively. ASTAP is free to trade centroid precision for speed because
"solved" is its whole output; we are not.

That distinction has teeth. ASTAP binned this very field 2x and solved it fine -- and we
deliberately will not (see phase D). Every phase below therefore states what it costs accuracy,
and the answer has to be "nothing" before it ships.

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

**The init row below is superseded -- see phase B.** "Tycho-2 bulk decode dominant" was wrong even
when written, or became wrong immediately after: that decode costs 0.3 ms of init, and the 270 ms
inside init is a different phase entirely.

| stage | time | share |
|---|---|---|
| fixed init (~~Tycho-2 bulk decode dominant~~) | ~590 ms | 51% |
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
`--quad-stars` knob). ~~It has never been pointed at the catalog side.~~ **It has, and that
sentence was stale when written** -- `StarReferenceTable`'s own XML doc had said so since
`edd18996`. What the two claims cost, and what the measurement settled, is phase C below.

## Phases

Ordered by return per unit of risk; each independently shippable and measurable.

| # | change | saves | costs accuracy? | risk |
|---|---|---|---|---|
| A | Cancel the losing parity (916k wasted hypotheses) -- **SHIPPED** | ~200 ms | nothing | low |
| B | ~~Tycho-2 pre-baked region index~~ -- **OBSOLETE: already done, worth 0.3 ms** | ~~500 ms~~ 0 | n/a | n/a |
| C | ~~Quad-descriptor matching replacing the refinement loop~~ -- **MEASURED DEAD: 15.8% of quads are shared even under a perfect prior** | ~~300 ms~~ 0 | n/a | n/a |
| C' | Quads for the SCALE only: recover it with no prior, narrow the seed's window 5% -> 0.25% -- **SHIPPED** | 7.4x fewer seed hypotheses | nothing, and it *removes* our reliance on `FOCALLEN` | low |
| D | Cap the star list to ~500 -> yes; bin -> **no** | ~60 ms | binning does, so it is gated on measured FWHM and default off | low |

Target after A-C: **~260 ms**, against ASTAP's 162 ms -- **written before B was measured to be
already done.** With B worth 0 and the warm solve now benchmarked at 331 ms, the reachable target is
whatever C takes off that, and the 1158 ms headline figure is itself stale: it bundled a cold start
that no longer blocks. Re-derive the goal from `PlateSolveBenchmarks` rather than from this table.

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

**Key it by the LIGHT PATH: the (OTA, camera) pair. Never by rig, and neither half alone.** Parity
is set by the number of reflections between sky and sensor, plus the sensor's own row-order
convention -- and those two facts live on different objects, which is why neither is a sufficient key.

- **Not per rig.** An off-axis guider's pick-off prism is a single reflection before the imaging
  plane, so on a refractor + OAG the main camera is even-parity and the guide camera is ODD --
  opposite parity, same rig, at the same time. Not hypothetical: the polar-align loop solves GUIDER
  frames while the session solves IMAGING frames, so one parity per rig is actively wrong for one of
  them.
- **Not per OTA alone**, for the same reason -- that OAG guide camera is on the same OTA as the main
  camera and must not share its answer.
- **Not per camera alone.** The optical train is the OTA's, not the camera's: swapping a camera body
  onto the same scope changes no reflection, while moving one camera from a refractor to an SCT with
  a diagonal changes parity for that same camera. A camera-only key gets both of those backwards.

So the key is the pair. The cost of getting it slightly wrong is deliberately low -- parity stays a
HINT, so a miss costs one unnecessary both-parity solve and self-corrects -- but a key that is wrong
by CONSTRUCTION would be wrong on a whole class of rigs forever, which is a different thing.

Store it the way per-focuser backlash is stored in `Profiles/BacklashHistory/`, keyed on the pair.

**Do not try to infer parity from the declared optical train.** Counting reflections is
insufficient even in principle, because FITS ROW ORDER contributes: a `BOTTOM-UP` frame read as
`TOP-DOWN` is a vertical flip, i.e. a parity change with no mirror anywhere in the optics.
Conventions differ across capture software. Learning parity from the first successful solve
captures optics and convention together, needs no user declaration, and is self-correcting.

**Parity stays a hint, never a constraint.** A stale cached parity (someone added a diagonal, moved
the camera to the OAG port, changed capture software) must not turn a solvable frame into a
permanent failure. Skip the other branch speculatively; fall back to trying both on failure. That
costs nothing in the common case and stays correct in the rare one.

#### What phase A measured, and what it actually did

The one-frame anecdote above (32,179 vs 915,994) understated it. `PlateSolveParityWasteProbe`
(`TIANWEN_PARITY_WASTE=1`) walks all 96 frozen Vela frames through both parities and reports:

| | hypotheses |
|---|---|
| winning parity | 8,089,876 |
| **losing parity** | **259,493,292** |
| waste share | **97.0%** |
| worst single loser | 2,977,624 (P13, summed over its three pool policies) |

And the structural fact that makes the phase safe: **exactly one parity locks on every one of the 96
frames -- never both, never neither.** So a claim can be granted on the first seed clear of chance
without any real risk of stopping the eventual winner.

Three things the implementation had to get right, none of them in the original sketch:

- **The acceptance gate CONSUMES the loser.** `ApplyAcceptanceGate` falls back to the losing parity
  when the winner fails the chance test, and logs "parity pick overturned" when it does -- so
  cancelling the loser removes a correctness fallback. An abandoned attempt's null WCS means "never
  finished looking", not "nothing there", so the gate re-runs it, uncancelled, in exactly that case.
  That re-run is what makes cancelling safe rather than a gamble; it costs a whole extra attempt and
  is reached only when the winner has already failed.
- **At most one attempt may ever be abandoned.** Two unguarded claims would cancel each other and
  lose both halves, failing a solvable frame. An `Interlocked` single-winner claim makes that
  unreachable rather than merely unlikely.
- **`SolveAttempt` is a `readonly record struct`, so `ReferenceEquals(loser, std)` is always false.**
  Which parity won is carried as a flag. The boxed-identity version compiles, runs, and picks the
  wrong half of the time.

`PairRansacLock.TryLock` now takes a token and checks it every 4096 hypotheses (not every one: it is
the innermost loop of the solve), leaving by the same path as the hypothesis cap and reporting
`Cancelled` so a caller can tell an abandoned scan from an exhausted one. `TrySeedPairLock` stops
descending its pool-policy chain once cancelled -- three pools each abandoned mid-scan would save
nothing.

**Coverage, stated exactly.** `PlateSolveParityRaceTests` solves the synthetic field twice, once
upright and once flipped on Y -- a parity change with no mirror in the optics, which is the same
thing a BOTTOM-UP frame read as TOP-DOWN is. The mirror attempt wins the first and the standard
attempt the second, so the pick demonstrably reads the image and neither parity is hard-wired.

**What no test here covers, checked rather than assumed:** the winner flag's two consumers (which
half counts as abandoned, which sign is re-run) sit inside the gate's fallback, and a solvable field
never reaches it. Reintroducing the `ReferenceEquals`-on-a-record-struct bug leaves both parity tests
GREEN -- verified, because a pair that watches each parity win looks like it must cover the flag and
does not. Reaching that branch needs a frame whose seed locks convincingly and whose refinement then
fails the chance test, which is not constructible on demand without fabricating it. The failure mode
if it is wrong is a recoverable miss (the factory falls through to ASTAP), never a wrong WCS.

**Not yet measured: the wall-clock saving on a real frame.** The 97% figure is the seed stage's
hypothesis count, which is deterministic and machine-independent; converting that to the plan's
~200 ms estimate needs the benchmark harness below, on a real FITS. `PlateSolveParityRaceTests`
proves the cancellation FIRES end to end (and fails when it is disabled -- the solved WCS is
byte-identical either way, so nothing else could).

#### The cache half -- **SHIPPED 2026-09-01**, and it is a BUDGET, not a skip

`SolveHintCache` remembers, per light path, what an ACCEPTED solve answered: the plate scale (as the
header-over-solved RATIO, the same quantity `QuadScaleRecovery.Recovery` carries, so it consumes
through the existing two-tier seed with no new path) and the parity. Keyed
`(Telescope, Instrument, RowOrder, BinX, BinY)` -- the pair as argued above, plus row order because it
is a parity determinant in its own right and belongs in the key rather than being learned, plus
binning because the same camera at 1x1 and 2x2 is two plate scales. In-memory on the solver, which is
a DI singleton, so a session's second and later solves benefit; persisting it the way
`BacklashHistory` is persisted would extend that to the first solve of a night and is not done here.

**A remembered parity SHRINKS the doubted half's hypothesis budget; it never skips it.** Skipping is
the obvious form and it is wrong in exactly the place this is meant to help. With one parity gone, a
frame that seeds on neither runs the two halves in SERIES -- the believed one, then the gate's re-run
of the other -- where today they overlap. The doomed path would get twice as long in wall clock to
save half the CPU, which is the opposite of the goal. Capped and still parallel, the wall time stays
the believed half's while the doubted half stops early. When the cache is stale the capped half simply
finds nothing, and it is marked abandoned, so the gate's existing re-run picks it up uncapped -- the
same fallback an abandoned parity has always used. `DoubtedParityHypothesisBudget = 100_000` is sized
off what a real lock costs (Vela panels 235-567, P10's relocated solve 749, phase A's own frame
32,179), not off what a scan can afford.

**Measured on the P10 crop, second solve on the same solver: 9,165,717 -> 5,813,171 hypotheses, 1.6x.**
Two things that measurement says, both worth keeping:

- **The saving is the PARITY half only, on a frame that fails.** A remembered scale cannot help a
  doomed pass by construction: the narrow tier is tried first, does not lock, and falls through to the
  header's +/-5% window, which is the expensive scan it was meant to avoid. That fallback is what
  stops a stale scale from turning a solvable frame into a failure, so the ordering stays.
- **The scale half is a net NEGATIVE on a doomed frame, at +13.6%** (9,165,717 -> 10,418,023 with the
  parity cap disabled): the narrow tier's own pass is pure addition when nothing can lock. The parity
  cap more than pays for it, and the scale's win lands on a frame that SOLVES while its quad recovery
  declines -- a real combination (2 of 5 archive frames decline) that no committed fixture reproduces,
  so it is deliberately not asserted. Worth revisiting: bounding the *cached-scale* tier the same way
  the doubted parity is bounded would make its failure cost negligible while keeping its win, since
  the wide-window fallback already covers a narrow scan cut short.

**The parity travels ON `SolveAttempt` (`IsStd`), not beside it.** The consumer that matters reads it
AFTER the acceptance gate, and the gate is the one place the pick gets overturned -- so a flag tracked
alongside the race would teach the cache the wrong half in exactly the case the gate exists to catch.
It also keeps phase A's own bug fixed for the same reason it was a bug: which half won has to be DATA,
never `ReferenceEquals` on a record struct.

Guarded by `SolveHintCacheTests` (learning, light-path separation, and that an unidentified frame or a
non-positive ratio teaches nothing) plus the second solve in
`RealFrameSolveTests.TheCropWhoseHeaderPointsElsewhereStillSolves`, which asserts on
`CatalogPlateSolver.LastSeedHypotheses` -- deterministic, unlike wall clock -- and was seen to FAIL
with the cap disabled.

### B. Tycho-2 pre-baked region index -- **OBSOLETE, the work is already done**

**Measured 2026-08-31 and it invalidates this phase.** The Tycho-2 bulk load, budgeted here at
~500 ms and called the largest single line item, costs **0.3 ms** of init. It runs to completion on a
background task before init needs it, and the region index this phase proposed BUILDING already
exists: `tyc2.bin` is stream count + per-GSC-region offset table + region-major 17-byte records, and
the build's `ExpandTycho2` target expands the `.lz` so reaching one region's ~59 KB no longer costs
decompressing all 43.5 MB. Both landed after this plan was written. Nothing here is left to do.

Full init breakdown, `InitDBAsync(waitForTycho2BulkLoad: true)`, 587 ms total:

| phase | ms |
|---|---|
| **tycho2-cross-ref-join** | **269.9** |
| hd-hip-cross (2A snapshot applying -- the FAST path) | 114.8 |
| ngc-csv | 66.5 |
| simbad-total (2B snapshot applying) | 54.5 |
| shapes | 38.3 |
| cross-ref-json | 17.9 |
| predefined | 16.9 |
| **tycho2-bulk-wait** | **0.3** |

#### What was actually wrong, and the fix (SHIPPED 2026-08-31)

`LoadCrossRefBinFile` still lzip-decompressed `hip_to_tyc.bin.lz` and `hd_to_tyc.bin.lz` on every
start. `tyc2.bin` got the build-time expansion and these two never did, and that was the entire gap:

| file | records | decompress | decode loop |
|---|---|---|---|
| `hip_to_tyc` | 120,404 | 83.9 ms | 39.9 ms |
| `hd_to_tyc` | 359,083 | 190.8 ms | 91.5 ms |
| total | 479,487 | **274.7 ms** | 131.4 ms |

`ExpandTycho2CrossRef` reuses the existing (already generic) `expand-tycho2.ps1`, and the loader
prefers the expanded resource with the `.lz` kept as fallback. **LFS-neutral by construction**, which
is the point: the committed `.lz` are LFS objects and are untouched read-only inputs, while the
expansion lands in `obj/` and is never tracked. Expanded they are 0.57 + 1.71 = 2.29 MiB, against the
43.5 MiB expansion already accepted next door.

Result:

| phase | before | after |
|---|---|---|
| tycho2-cross-ref-join | 269.9 ms | **0.0 ms** |
| **total init** | **587 ms** | **343 ms** |

#### The string round trip: "saves nothing" was measured on the wrong axis (FIXED)

I first argued this half was pointless, because with the join at 0.0 ms its ~131 ms runs in the shadow
of the main-thread phases. That is true of WALL CLOCK and false of everything else. Measured with
`GC.GetAllocatedBytesForCurrentThread`, the per-record round trip allocated **twice** per star:

| | before | after |
|---|---|---|
| `hip_to_tyc` (120,404 records) | 8,301,888 B | 963,640 B |
| `hd_to_tyc` (359,083 records) | 25,394,712 B | 2,872,728 B |
| **total allocated** | **33.7 MB** | **3.84 MB** |
| **Gen0 collections** | **11** | **4** |
| decode loop | 164.8 ms | 117.6 ms |

70 bytes per star, to produce eight. The two allocations were the base91 **string**, and a **box**:
`AbbreviationToEnumMember<T>` ends in `(T)Enum.ToObject(typeof(T), ...)`, which boxes the ulong and
unboxes it back. `CatalogUtils.Tyc2CatalogIndex` avoids both -- the chars go to a `stackalloc` via a
new span-writing `Base91.EncodeBytes` overload, and knowing the concrete type (`CatalogIndex : ulong`)
makes the enum conversion a free cast where the generic helper must box.

**The remaining 3.84 MB is exactly the output**: 479,487 x 8 = 3,835,896 B against 3,836,368 measured.
Transient allocation is now zero, which is the useful stopping condition -- not a ratio.

Two things worth carrying forward. **Gen0 is stop-the-world**, so those 11 collections were pausing the
main thread while it did ngc-csv and simbad work; "off the critical path" was never quite true even for
wall clock. And the boxing is in the GENERIC helper, so every other
`AbbreviationToEnumMember<ObjectType|Constellation|OpenNGCObjectType>` caller in the catalog parse pays
it too -- deliberately not touched here, because that helper is used across several enums of different
widths and a wrong reinterpret there corrupts identifiers silently. Fix it separately, with its own
proof.

`Tyc2CatalogIndexTests` pins the new path byte-identical to the string round trip across ~49,000
(TYC1, TYC2, TYC3) combinations plus the boundaries base91's own 13-vs-14-bit branch keys on. A
`CatalogIndex` indexes every catalog dictionary, so one differing value is a star that can never be
looked up again and nothing would throw.

The new largest item is `hd-hip-cross` at 121.8 ms, and note that is already the 2A snapshot's FAST
path (read 48.8 + apply 72.0), so it is a different kind of target from a decompression that should
never have been happening.

**The original bottleneck claim, for the record:** (`hip_to_tyc` + `hd_to_tyc`), and note what the
270 ms IS: a `await tycho2CrossRefTask`, i.e. the main thread BLOCKED. That task is already kicked off
first and already overlapped with every other main-thread phase, so the 270 ms is what remains after
all the overlap there is -- its own wall is ~446 ms. Making init faster therefore means making that
task cheaper (a 2A/2B-style pre-baked snapshot of its output is the obvious move), not indexing
Tycho-2, which is done. The code comment calling those arrays "cheap to decompress" is the assumption
this measurement contradicts.

**Whether to do it at all is a separate question, and the answer is probably not yet.** Init is paid
ONCE per process; phase C is paid per solve. A night solving ten frames saves ~270 ms from a perfect
init fix and ~3,000 ms from phase C. Init only wins on PERCEIVED latency, since the first solve is the
one a user is watching.

### C. Quad-descriptor matching against the catalog

**Measured 2026-08-31 by `QuadCatalogFeasibilityProbe` (`TIANWEN_QUAD_FEASIBILITY=1`), and it splits
this phase in two: the matching half is dead, the SCALE half is alive and is the half this plan
actually cared about.**

The phase was written as: build quads from the top-K detected stars, build quads from the catalog
window, match on the five scaled distances, take the plate scale from the median longest-side ratio,
and let that replace the iterative proximity loop -- removing the dependence on the header scale on
the way, since `FOCALLEN` is only ever a hint.

The premise needed checking first, because this plan and `StarReferenceTable`'s XML doc contradicted
each other outright: the plan said the quad matcher had never been pointed at the catalog side, and
that doc said it had, with the answer "no quad lock at any K from 50 to 500 in either parity". So the
probe grants quad matching **every** confound in its favour -- the catalog is projected through each
frame's OWN frozen solution, so the two point sets share a pixel frame exactly: same scale, same
rotation, same translation, same parity, zero hint error. The headline is then tolerance-free, because
under a shared frame a genuinely corresponding quad has the same CENTRE, so coincidence can be counted
with no matcher and no threshold in the way.

| top-K | image quads | catalog quads | stars shared | **quads shared** | existing `FindFit` |
|---|---|---|---|---|---|
| 100 | 1,629 | 1,217 | 63.2% | **42 (2.6%)** | 0/24 panels |
| 200 | 3,414 | 2,624 | 63.4% | **124 (3.6%)** | 0/24 |
| 300 | 5,230 | 4,242 | 66.8% | **414 (7.9%)** | 0/24 |
| 500 | 8,989 | 7,898 | 73.2% | **1,419 (15.8%)** | 6/24 |

**Both documents were partly right, and neither conclusion was safe.** Quads ARE shared, so "no lock
at any K" was not a property of the data -- at K=500 the existing matcher locks 6 of 24 panels. But it
locks them only because the oracle projection makes `Dist1` comparable to 0.1%: `StarQuad.WithinTolerance`
compares `Dist1` in absolute px against the same tolerance as the five ratios, a mixed-unit test its own
doc admits "works because stacking images have near-identical Dist1 values". Under a real hint it does
not, which is what the earlier probe was measuring.

And the population argument holds where it matters: 73.2% of stars are shared but only 15.8% of quads,
far below the 0.73^4 = 28% that independent membership would give. One interloper in a neighbourhood --
a catalog star too faint to detect, a detected artefact absent from the catalog -- re-wires which four
stars are mutually nearest, so the quad is not the same quad. **That is why the matching half cannot
replace the refinement loop**, on two counts: 15.8% under the most favourable conditions obtainable
against the pair seed's 96/96, and a quad match yields quad CENTRES, where the CD matrix and the SIP
fit consume hundreds of sub-pixel STAR correspondences (the loop produces 300-1,400 per frame).

**What the shared quads do say is that the descriptor and the scale are excellent when the four stars
survive:** worst-of-five-ratios error median 0.0004-0.0010 and p90 <= 0.0039 against the 0.008 default
tolerance, with the implied scale (the `Dist1` ratio) at median 0.9998-1.0001 and p10-p90 inside +/-0.14%.

#### C'. What survives: recover the plate scale with no prior

The seed needs a scale prior *because a pair length has units* -- `MinPairLockScaleTolerance`, +/-5%,
and the comment there already names quads as the structural answer. A quad descriptor is five ratios,
which are scale-free, so a matched quad hands the scale back as the ratio of the two longest sides.
This needs a HANDFUL of matched quads, not hundreds of correspondences, which is exactly the regime the
table above says is available.

Measured under the production condition rather than a favourable one -- catalog through the header hint
(pointing wrong by up to 40 arcmin across this mosaic, unrotated where the real fields are rotated) and
the pixel scale deliberately wrong by **3.9%**, the marketed-versus-actual focal length this plan
already records, with `Dist1` **never compared** so no scale is assumed:

| ratio tolerance | comparisons | candidates | recovered (truth 1.039) | panels within 1% |
|---|---|---|---|---|
| 0.002 | 2,919,284 / 24 panels | 44/panel | **1.0360** | **23/24** |
| 0.004 | " | 52/panel | **1.0361** | **23/24** |
| 0.008 | " | 56/panel | **1.0362** | **23/24** |

~122k comparisons per panel, which is the same order as ASTAP's ~110k, and the answer lands 0.27% low
on a prior that was 3.9% wrong. Rotation is not a confound and that is the point: a distance is
invariant under rotation.

**What it buys, measured on the same frames.** A window is a WIDTH and a CENTRE, and sweeping the
width alone measures the centre -- which is how the first version of this table read as a floor that
was not there:

| scale window | declared centre | recovered centre |
|---|---|---|
| +/-5% (shipped) | 24,923,440 / 23 locked | 24,928,390 / 23 |
| +/-2% | 16,085,840 / 24 | 16,113,394 / 24 |
| +/-1% | 8,835,750 / 23 | 8,841,502 / 23 |
| +/-0.5% | 5,165,820 / 23 | 5,187,187 / 23 |
| +/-0.25% | 5,440,956 / **10** | **3,350,087 / 23** |
| +/-0.1% | 4,173,986 / **2** | **2,239,472 / 23** |

Hypotheses over 48 parity attempts; locks flat at 23-24, i.e. exactly one parity per panel, the fact
phase A rests on. Against the DECLARED scale locks survive to +/-0.5% and then collapse, because the
declared scale is itself 0.26-0.31% off the solved one on every panel of this mosaic, so a +/-0.25%
window excludes the answer. Against a recovered centre all six hold and hypotheses fall
monotonically to **11.1x**. The reason the shipped window is 5% is the unreliability of `FOCALLEN`,
not any property of the sky.

An earlier note here predicted a floor at ~0.5% from the `+/-3 px` absolute term in
`PairRansacLock`'s admission band, reasoning from the 601 px minimum baseline where 3 px IS 0.5%.
Measured, that is wrong: the pair POPULATION is mostly much longer baselines (to the ~4,000 px
diagonal, where the fractional half-width at 0.5% is 20 px against the same 3 px), so the fraction
still rules for most pairs. The floor binds the shortest baselines only.

The correctness half is the better argument. The header said 205 mm because that was typed into the
profile by mistake while the optics are 202.5 mm (1.2%), and a 130 mm lens was entered as its
MARKETED 135 (3.9%) -- which is systematic rather than a typo, so it recurs, and it put a 3,065-star
frame with 1,197 catalog stars in it outside the old 3% window entirely, so it did not solve at all.
A scale read off the stars removes that failure class rather than widening a window to survive it.

**Two things quads cannot do, so nothing downstream should be built expecting them.** They cannot
settle the PARITY -- reflection preserves distances, so a mirrored field has identical descriptors and
the parity race stays exactly as phase A left it. And they cannot supply the refinement loop's
correspondences, per the table above.

#### What shipped

`QuadScaleRecovery` (`Astrometry/PlateSolve/`), wired into `CatalogPlateSolver`:

- **The recovery runs ONCE, before the parity race.** It is parity-blind, so computing it inside each
  attempt would do identical work twice and could disagree with itself.
- **The guard is the SPREAD, and the candidate count is INVERTED.** `MaxRelativeSpread` = 0.01 (MAD
  about the median over the median). Across the 24 panels the 23 accurate ones sit at 0.0004-0.0014
  and the one bad one at 0.3699 -- a 264x gap. That bad panel produced **92** candidates, more than
  any good one (26-74), so a count threshold would have singled out the only untrustworthy recovery
  as the most trustworthy: contamination is chance ratio agreement, which is plentiful and scatters,
  where real shared quads are scarce and agree. `IQR/median` fails too (0.2968 and 0.1583 on panels
  that recover to 0.006% and 0.001%) because its percentiles sit in the contaminated tails.
- **`RecoveredScaleTolerance` = 0.25%, not the 0.1% the sweep permits.** The sweep centred on each
  frame's own SOLVED scale; the recovery delivers 0.065% worst case, so 0.1% would leave 0.035
  percentage points of slack over a one-dataset error estimate. 0.25% keeps 3.8x margin for 7.4x
  fewer hypotheses, and the last 1.5x is what an EXACT prior would earn -- a scale carried forward
  from a previous solve, which belongs with phase A's unshipped per-(OTA, camera) parity cache rather
  than with a fresh estimate.
- **The correction applies to the WHOLE attempt, not just the seed.** The seed's transform, the origin
  `InverseTanProject` derives from it and the CD matrix `AttachCDMatrix` builds all live in one
  projection's space; correcting the seed alone mixes two, which is the shape of both bugs the
  acceptance gate originally found here (the origin-convention bias and the SIP reference-pixel
  mismatch).
- **A failed narrow seed RETRIES on the header's scale at +/-5%.** That is what makes the narrow
  window safe to attempt at all: a stale or unlucky recovery costs one extra pass that is cheap by
  construction (2.2M hypotheses against 24.9M) and can never turn a solvable frame into a failure.

On the committed NGC 3576 frame the recovery fires and beats the header by 2.5x: `FOCALLEN` implies
2.8724"/px, the recovery returns 1.00269 from 27 candidates at spread 0.0017 (2.8647"/px), and the
solve lands at 2.8669 -- 0.077% out, inside the 0.25% window with 3.2x margin.

**`PlateSolveBenchmarks` cannot resolve this, and hypotheses stay the unit.** Run after the change it
reports the full hinted solve at a MEAN of 272.9 ms with an Error of **+/-98.3 ms** (36%) and a 514 ms
outlier, on a box that had been building all session. The 331 ms this plan quotes above is a MEDIAN on
a quiet box, so the two are not comparable -- reading a 331 -> 273 improvement off them would be
precisely the cross-build wall-clock comparison that is wrong here. The harness's resolution was
always stated as right for a 2-4x whole-solve target and not for a 10% delta, and the seed is only a
part of the solve, so a 7.4x cut in SEED hypotheses is expected to sit under that floor. Anyone
wanting the ms must run before and after in one sitting on an idle machine.

**The negative case for a SCALE estimator is not the obvious one.** Asserting that an unrelated field
must yield NO scale is wrong, and writing it that way is how this got found: 8 of 24 non-overlapping
panel pairs answer confidently and every answer is RIGHT, because both panels come from the same
camera through the same optics, so two unrelated patches of sky genuinely share a plate scale.
Positional unrelatedness is `PairRansacLock`'s problem. The hazard that matters is a confident ratio
that is not the true one, so `QuadScaleRecoveryTests` shifts the second field's scale instead: at
x0.80 the answer tracks the shift, at x1.25 it declines outright, and it never invents ~1.0 from
chance agreement.

#### What a solve costs now, cold and warm (measured 2026-09-01)

`SolveTimingProbe` (env-gated `TIANWEN_SOLVE_TIMING`) solves five committed real frames six times over
per process, three processes, medians. It exists because the two costs a session pays are different
and **BenchmarkDotNet can only ever see one of them**: the catalog is cached per process, so a BDN
second iteration is already a warm start, and the cold half is the 51% row in the budget above.

Cold -- first frame in a fresh process (NGC 3576, the most expensive of the set). Excludes host startup:

| stage | ms |
|---|---|
| catalog init (ONCE per process) | 266 |
| FITS load, 10 MB gz -> 3008x3008 | 184 |
| solve (query 30 / detect 110 / quad 13 / seed+refine 134) | 312 |
| **cold, to a WCS** | **762** |

Warm -- the same process, solve only:

| frame | scale | detected | warm | cold pass 1 |
|---|---|---|---|---|
| NGC 3576, SH61 270 mm, 3008x3008 GRBG | 2.87"/px | 8103 | **221 ms** | 312 ms |
| HD 71216, Samyang 130 mm, 3008x3008 | 5.97 | 1932 | **106** | 168 |
| Horsehead, Samyang 135 mm, 3008x3008 (SharpCap) | 5.74 | 2227 | **60** | 165 |
| Vela P8 crop, 2354x2150 mono | 5.97 | 4003 | **120** | 248 |

Cold-to-warm on the solve alone is 1.4x-2.7x and it is **all JIT** -- the second solve of ANY frame is
warm, not only of the same one. Share of a warm solve: seed+refine 43-61%, detection 34-47%, catalog
query 1.4-3.7%, quad recovery 0.9-2.0%. So **phase B really is finished** (2-6 ms), and **C' costs
1-2 ms to remove 7.4x of the seed's work**.

**Recovery fires on 2 of these 5 frames, and the gate that refuses is the CANDIDATE COUNT, never the
spread.** Where it answers: 27 and 18 candidates at spread 0.0017 / 0.0004 against the 0.01 bar. Where
it declines: 8, 6 and 4 candidates against `MinCandidates = 10`, deterministically, same counts every
run. The guard that protects correctness is therefore carrying 6-25x of margin while the count gate
does all the refusing, which makes `RatioTolerance = 0.004f` the knob worth loosening -- coverage
would rise and the spread guard would still refuse a wrong answer. It matters because the frame it
declined on is the one that needed it most: the SharpCap Horsehead states `FOCALLEN = 135`, implying
5.7449"/px against a solved 5.9744 -- **4.0% wrong**, saved only by the +/-5% fallback. The same
recovery fires on 94 of 96 frozen Vela frames, so the low coverage is a property of real captured
frames, not of the method.

**The failure path costs 4.0 s, 65x a warm success**, because the refinement loop runs to exhaustion
before the acceptance gate refuses. The frame that prices it, the `Vela_SNR_Panel_10` crop, turns out
to be a real gap in this solver -- and **the obvious experiment gives the wrong answer about it**. It
does not solve at any search radius out to 12 deg (75,062 catalog stars) nor at any scale from 0.5x to
2x the declared one, which reads as "not pointing, not scale". Both sweeps are misleading. ASTAP
solves the same file in 0.7 s with 71 of 72 quads matched, and its solution says why: the crop's
header points **93 arcmin** from where the frame actually is, on a **2.17 deg** field. That is 70% of
the frame width, leaving the hint-projected frame overlapping the real one by **26%**. Hand our own
solver ASTAP's centre and it solves in **23 ms** with 613 stars matched -- faster than anything else
in the set.

**A wider search radius cannot fix a bad hint, which is the whole trap.** `searchRadius` widens the
catalog QUERY, while the seed's anchor pool is the brightest catalog stars that project INSIDE THE
FRAME from the hint -- so the pool's footprint is the hint's own frame however wide the query gets,
and three quarters of it is off-frame and undetectable by construction. That is the same failure mode
the margined anchor pool exists for, at an offset far past what a 10% margin reaches. ASTAP does not
have it because its `-r` means "search a spiral of sky POSITIONS around the hint", not "query wider".
The control sits right beside it: the P8 crop's header is 12 arcmin off (5% of its frame width, 93%
overlap) and solves normally. **So the missing capability is a positional search around the hint**,
not a bigger radius and not a bigger catalog -- worth its own phase if a wrong-by-a-field-width hint
is a case worth serving, which for a mosaic crop or a badly-synced mount it is.

**The header is not mis-annotated -- it is accurate about a different thing.** `OBJECT = HD 72800`
with `OBJCTRA`/`OBJCTDEC` at 08 32 56 / -47 36 17, and the catalogued HD 72800 sits **0.4 arcmin**
from exactly that (P8's `HD 74167` likewise, 0.3 arcmin). The pointing record is faithful; it records
where the SCOPE WAS AIMED for the panel, and these pixels are 93 arcmin from it. The frozen star lists
settle where that came in: the full panel 10 has a hint error of **0.3 arcmin**, so the mount's own
pointing was excellent and the offset is introduced by the crop, which kept the panel's header. A
consumer cannot tell: nothing in a FITS header states that `OBJCTRA` still describes the frame after
someone crops it, which is the same class of trap as `RA`/`DEC` not being the frame centre.

**What actually predicts a seed failure is hint error as a FRACTION OF FRAME WIDTH**, and the frozen
set brackets it:

| | hint error | frame | as % of width | seeds? |
|---|---|---|---|---|
| P8 crop | 8.1 arcmin | 3.90 deg | 3.5% | yes |
| worst full panel (P15b), of 96 | 58.7 arcmin | 4.98 deg | 19.6% | yes |
| P10 crop | 93.0 arcmin | 2.17 deg | **71.4%** | no |

The seed's two anchor-pool policies carry margins of 0% and 10%, so tolerating ~20% is about what the
margined pool plus the frame's own slack buys, and 71% is far outside it. **That is the number to
design against** -- not the absolute arcminutes, which is why a 4.98 deg panel survives an error seven
times larger than the one that kills a 2.17 deg crop.

**Narrowband is NOT implicated, and the quad-candidate count is not an independent signal.** These
panels are HOO (Ha into R, OIII into G and B), so "the anchor pool is ranked by a broadband magnitude
the image does not measure" was the natural competing explanation, and P10's 4 agreeing quad
candidates against P8's 18 looked like its evidence. It is not: `QuadScaleRecovery` projects the
catalog through the SAME hint the seed does, so a 74%-off-frame projection starves the quad list by
exactly the same mechanism. Re-hinted, the identical HOO pixels give **17 candidates at spread
0.0005**, an rms of 0.19 px and 613 matched stars. **So read a declined recovery as evidence about the
HINT as much as about the frame** -- which is worth remembering next to the real-frame decline counts
above, where the header's pointing was never separately checked.

Nothing regressed: P10 had never been asked for a WCS before (it is a stretch / colour / codec fixture
everywhere else), which is exactly why the gap had gone unmeasured.

### D. Cap the star list; bin ONLY where sampling allows it

Two halves with very different risk, and they must not be conflated.

**Cap the star list: do it.** Carry ~500 brightest into matching instead of 1600. This costs no
accuracy at all -- the discarded stars are the faintest, they are not what a fit is anchored on, and
ASTAP solves this field from 527. Phase C makes this mostly moot anyway, since quad matching is
driven off the top-K.

**Bin before detection: NO, not on this class of frame, and not on ASTAP's rule.**
`report_binning` gates on image HEIGHT (> 2500 px -> bin 2). Height is the wrong variable: it is a
proxy for "big sensor, therefore probably oversampled", and this rig breaks the proxy. Measured on
frame 0018, median star **FWHM = 2.15 px** (10.16" at 4.7172"/px; HFD 2.65 px, from
`solve --export-stars`). That is already AT critical sampling, so binning 2x lands at 1.08 px FWHM
-- below Nyquist. The cost is not a slightly softer centroid, it is aliasing, and a 1 px FWHM star
stops being reliably separable from a hot pixel. Our unbinned SIP rms is 0.11 px (0.52"); that is
the number downstream consumers are relying on.

So if this is done at all, gate it on MEASURED sampling rather than on frame size: bin only while
the binned FWHM stays comfortably above Nyquist, i.e. roughly FWHM >= 4 px before binning. A
0.5"/px setup in 3" seeing (FWHM ~6 px) can bin to 3 px for free; this field cannot bin at all.
Detection already measures FWHM/HFD, so the input is in hand.

Default OFF, and skip the whole phase if phases A-C land the budget. It is the smallest win of the
four and the only one that can cost accuracy.

### E. Positional search around the hint -- **SHIPPED**

A hint wrong by a large fraction of a frame width is unsolvable today however generous the catalog
query, because the seed's anchor pool is the brightest catalog stars that project INSIDE THE FRAME
from the origin: the pool's footprint IS the origin's frame. `searchRadius` widens the QUERY, which
is why sweeping it to 12 deg (75,062 stars) changes nothing. So the fix is a search over POSITIONS.

`SolveImageAsync` now delegates to `SolveFromOriginAsync(..., allowPositionalSearch)`. When a solve
fails, `TryFindOriginByPositionalSearch` walks candidate centres outward from the hint until one
seeds, and the solve re-enters ONCE with that origin -- re-entering rather than resuming mid-solve
because everything downstream of the query is a function of the origin, and `FindStarsAsync` is
cached on the `Image`, so the retry pays the query, the quad recovery and the race but not detection.

**The search returns an ORIGIN, not a solution**, and that is what licenses everything below. Whatever
it finds is re-seeded at full fidelity by the retry and still has to clear the acceptance gate, so a
coarse search can MISS a lock but cannot admit a wrong one.

**Geometry, not constants.** A frame reaches `0.5 * pixelScale * hypot(w, h)` from its own centre, so
the query covers `searchRadius + halfDiagonal` exactly. The `0.75 * max(fov)` the ordinary query uses
is the single-centre form of that same quantity (1.628 deg against a true 1.524 deg, ~7% slack).
Candidate spacing is `0.35 * min(fov)`: on a square grid the worst distance to a node is `step/sqrt(2)`,
so every point sits within ~25% of a frame width of some candidate, and the frozen mosaic brackets the
tolerance at 19.6% seeding / 71.4% not.

**Sizing the query is a CORRECTNESS requirement, not an optimisation**, and this is worth writing down
because the instinct is the opposite. Measured, the query is sub-linear in area and its cost per star
FALLS as the radius grows:

| radius | area | stars | ms | us/star | vs area-proportional |
|---|---|---|---|---|---|
| 1.63 deg | 8.3 | 2,436 | 5.2 | 2.14 | 1.00x |
| 3.69 deg | 42.8 | 9,605 | 13.7 | 1.43 | 0.51x |
| 12.0 deg | 452.4 | 75,062 | 66.0 | 0.88 | 0.23x |

13.7 ms against a 60-220 ms solve. Do not spend a day optimising it. (The general query path does
re-read each overlapping GSC region's whole entry list per 1x1 deg cell, where the polar path collects
unique regions and walks each once -- real, and paid for by locality, so not worth chasing either.)

**Getting the cost down took three measured rounds and the first estimate was 60x wrong**, which is
the part worth remembering:

| | failure path |
|---|---|
| before phase E | 4.0 s |
| naive search (full-fidelity seed per candidate) | **230 s** |
| + one anchor-pool policy, 250-star verification set | 15.9 s |
| + a 20,000-hypothesis budget per candidate | **5.8 s** |

Two facts behind that. `PairRansacLock` verifies every hypothesis against the WHOLE detected list
(1,569 stars on this frame) while capping ANCHORS at 48, so the verification set is what scales with
field density -- truncating it is the single biggest lever. And `MaxHypotheses` is **1,000,000**; the
"4096" in the diagnostics is a cancellation-check interval, not a budget, so a candidate centred on
empty sky was free to spend the real seed's entire allowance. A candidate centred on the truth locks
in the low hundreds (195 on the frame this exists for).

**An explicitly supplied `searchRadius` is a BOUNDARY, and bounding the candidates is not enough.**
`PlateSolverTests.GivenDenseFieldAndWrongSearchOriginWhenCatalogPlateSolvingThenItFailsInsteadOfReturningGarbage`
caught this: it hints 6 deg off with `searchRadius: 2.5` so that no correct correspondence is in
scope, and the search found the true field anyway. A frame centred at the edge of the search area
still reaches half a diagonal beyond it, and the disc anchor pool reaches further again -- so the
ANSWER is checked too, and a relocated solution outside the caller's radius is discarded. A caller who
passes a radius is saying "the truth is within this of my hint"; a field outside it, however real, is
not an answer to their question. (The ring loop also had to clamp: `ceil(radius/step)` puts the
outermost ring past the radius.)

### Gating the search on the seed's signal: TRIED, and the premise is wrong

The obvious way to remove the remaining +1.8 s was to gate the search on the direct seed's own
signal -- "a misplaced hint scores above chance, a frame with nothing in it scores AT chance, so
read `SeedCost.BestHits`/`ExpectedChanceHits`". It was built (threaded up through `SolveAttempt` to
the gate) and then **reverted, because measurement refuted every leg of it.** Three findings, and
the third one is the one that settles it.

**1. The populations do not separate, under any normalisation.** Three frames, the seed's best
consensus at the hint, against both the chance rate and against `PairRansacLock`'s own accept bar
(which is `max(max(10, 5 x chance), 0.15 x census)`):

| frame | detected | best hits | chance | bar | vs chance | vs bar |
|---|---|---|---|---|---|---|
| P10 crop -- hint 71% of a frame off, RELOCATABLE | 1,569 | 32 | 7.5 | 37.3 | 4.3x | **0.86** |
| dense field solved 6 deg off -- real sky, elsewhere | 3,250 | 22 | 2.9 | 24.0 | 7.6x | **0.92** |
| random 48-star field -- NO sky in it at all | 48 | 8 | 0.0 | 10.0 | 8.0x | **0.80** |

By chance ratio the frame with no sky in it scores the **highest** of the three: the model assumes an
independent field, so a real one clusters above it, and a sparse one drives chance to ~0 where 8 hits
is an enormous multiple of nothing. Against the bar -- the composite that stays meaningful in both
regimes, which is why the lock uses it -- the no-sky frame lands at 0.80 against the 0.86 of the case
that must never be refused. There is no threshold in that gap worth a shipped capability.

**2. The middle row is not noise, it is the shape of the problem.** A real star field scores high
whether or not it is *this frame's* field, because the seed measures "do these detections correlate
with a star field" and not "is that field mine". So a gate tight enough to refuse the 6-deg-off case
would refuse relocation too. That case is also the one where the search is *right* to run: there is a
field, it simply is not within the radius the caller allowed, and the radius check is what refuses it.

**3. The case it was written for never reaches the gate.** A cloudy frame mid-session has a GOOD
hint, so it does not fail the way the premise assumed. Measured on `FakeCameraDriver`'s cloud model at
the correct pointing: 0%, 70%, 85%, 93% and 97% coverage all **solve**, in 168-334 ms, still finding
90-130 stars through the worst of them. A frame with too few stars to seed exits before the race on
`detectedStars < MinStarsForMatch`, which is also before the search. The +1.8 s is paid only by a
solve that genuinely fails with a real field on the sensor -- which is the case that deserves it.

So **the regression is narrower than it looked, and the seed cannot be the discriminant.** What
remains available, in order of how much it would buy:

- **Bound the cost rather than predicting the outcome.** The failure path spends 4.0 s in proximity
  refinement before the acceptance gate refuses it, which is 2.2x what the search costs on top.
- **Let the caller state its pointing uncertainty.** An explicit `searchRadius` already clamps the
  candidate walk, and today exactly one production caller passes one at all (`Session.Focus.cs:882`,
  at 10 deg); polar alignment, `MasterPostProcessor` and the viewer pass none, so every session solve
  searches the full frame width. A session that has just slewed knows better than that.

**One thing the attempt did find, worth keeping:** on the random 48-star field the positional search
**seeded** -- it returned an origin for a frame with no sky in it, and only the acceptance gate on the
retry refused the result. That is the "returns an ORIGIN, not a solution" design being load-bearing
rather than decorative, and it is an argument against ever letting a search result skip the gate.

### Where a doomed solve actually spends its time -- it is the SEED, not the refinement

Measured 2026-09-01 off the solver's own stage logs (Debug build, so read the SHARES, not the
absolute times):

| stage | P10 crop, doomed pass at the header hint | dense field, hint 6 deg off |
|---|---|---|
| catalog query + detect + quad recovery | 195 ms | 377 ms |
| **seed + refine** (the one stage log) | **20,483 ms (98%)** | **8,669 ms (91%)** |
| positional search | 54 ms (0.3%) | 423 ms (4.5%) |
| relocated retry, whole solve | ~60 ms | n/a |

**Two attributions in this plan were wrong, and both pointed at the wrong lever.**

**The refinement loop does not run to exhaustion.** Both doomed frames exit it after **3** iterations
on each parity (P10: std 534 matches / 3 iters, mirror 518 / 3; the dense one: 2,652 / 3). The
divergence and convergence breaks already work. What fills that stage is the pair-lock SEED: with no
lock to be had it runs all three anchor-pool policies on both parities to 100% pair coverage --
1.39M-1.62M hypotheses per pool-parity, ~9.2M in total on P10. Nothing cancels, because the parity
race only cancels the loser when the winner seeds clear of chance, and on a doomed frame neither does.

**The positional search is not the +1.8 s regression it was recorded as**, at 54 ms on P10 (it seeded
at candidate 2) and 423 ms walking all 34 candidates on the dense one. Whatever produced the 4.0 s ->
5.8 s delta above does not reproduce here; the search is 0.3-4.5% of a failure path that is ~95% seed.
So the thing task #25 was written to remove was ~2% of the cost, and the thing to remove is the seed's
doomed scan.

**Which does NOT mean lowering `MaxHypotheses`.** The counter-evidence is already in this file: 400k
"stopped at 37-41% of pairs on real Vela frames and found no seed on fields that do have one". A hard
frame can need deep coverage, so the budget is load-bearing for exactly the frames it looks wasteful
on. The lever that survives is **phase A's unshipped half, the per-(OTA, camera) parity cache**: the
parity is a property of the optical train, not of the frame, so a rig that solved once has already
answered it -- and it is the doomed path that needs it, since a successful solve already cancels its
loser mid-seed. **Shipped, and it came in at 1.6x rather than the 2x this predicted** (the doubted
half is capped, not skipped, and the retry after a relocation runs a race of its own): see the cache
subsection under phase A.

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

**`PlateSolveBenchmarks` SHIPPED** (2026-08-31), so these figures are no longer hand-timed. Frame:
NGC 3576, SVBONY SV605CC / IMX533 3008x3008, SH61 EDPH 270 mm f/4.5, 60 s, N.I.N.A. -- already
committed for `FindStarsBenchmarks`. Solved as a raw GRBG mosaic, which is the shipped path
(`FindStarsAsync` debayers to mono internally); `SensorType.RGGB` means only "this is a CFA mosaic",
the pattern riding in the Bayer offsets.

Warm-catalog baseline, Release, medians (see below on why the median):

| | median |
|---|---|
| full hinted solve | **331 ms** |
| detect stars (incl. mono debayer) | 36-48 ms |
| catalog region query | 7.1 ms |

Plus, from `RealFrameSolveTests`, the two things BDN cannot report: **catalog cold start ~530-690 ms**
(51% of the budget, all of phase B -- once per process and cached after, so a second BDN iteration of
it is a warm start) and the scale the stars recover, **2.8669"/px against the 2.8724"/px `FOCALLEN`
implies**. That 0.19% gap is this plan's own point about the header scale being a hint: 270 mm was
typed in, the optics are ~269.5 mm, and the solver found it without being told.

**Two traps this harness hit, both worth knowing before writing another one.**

`Image` caches its `StarList` in a single slot keyed on the detection parameters, so the first version
measured a cache-key comparison: **50.87 ns to find 1,377 stars in a 9 MP frame**, and the "full
solve" silently stopped including detection from its second iteration. `[IterationSetup]` invalidating
the cache fixes it and is also the honest model, since a session solves a different frame every time.
A benchmark that measures nothing is worse than no benchmark, because someone will optimise against
it.

And **read the median, on a quiet box.** One invocation per iteration (forced by `[IterationSetup]`)
on work this long is high-variance: medians repeated to within ~3% across runs while the means moved
12%, and the detect benchmark under `ShortRunJob` reported an Error larger than its own mean. Hence
`RunStrategy.Monitoring` with 12 iterations. The resolution is right for what the phases target
(2-4x), not for calling a 10% delta.

## Explicitly not in scope

Matching 162 ms exactly. ASTAP is a mature dedicated solver with its own on-disk catalog format;
the goal is to stop being 7x slower, not to win. Phases A and B alone reach roughly 460 ms with no
algorithmic risk, which is already the difference between "noticeable" and "unnoticed" in a session
that solves once per target.

## The frozen Vela star lists, and why they are lists rather than frames

`TianWen.Lib.Tests/Data/vela-mosaic-starlists.json.gz` (2.1 MiB) holds STAR LISTS -- not FITS -- from
24 real Vela mosaic pointings / 96 frames / 78k catalog stars: per-frame detected centroids + the
gate-verified WCS (incl. SIP) as an oracle, plus one mosaic-wide catalog so a catalog index is the same
physical star in every panel. `VelaMosaicFieldTests` drives `CatalogPlateSolver.TrySeedPairLock` and
`PairRansacLock` over them; `VelaMosaicStarListExport` (env-gated, needs the user's archive)
regenerates the file.

**The suite costs 24 s, and exactly ONE test still searches a wide scale window.** It cost 115 s until
`SeedsFromHeaderHintAndAgreesWithTheFrozenSolution` and `BothAnchorPoolPoliciesAreNecessary` were
re-pointed at the SHIPPED two-tier seed -- quad recovery, then `RecoveredScaleTolerance` about the
recovered scale, falling back to `MinPairLockScaleTolerance` about the header's when it declines --
which is 6.7x and 5.2x faster AND more faithful, since the +/-3% they used was a window of the tests'
own invention that production never ran. It also turns these 96 frames into a LOCK-level guard on C'
(94 of them take the quad tier). That the recovery keeps FIRING is guarded next door by
`QuadScaleRecoveryTests`' recovered-panel count -- without it, a recovery that started declining
everywhere would quietly fall back to +/-5% here and still pass.

`UnrelatedDenseFieldsMustNotLock` keeps +/-3% deliberately, and is the one that must: a false lock is
what the pair matcher exists to refuse, and a WIDE scale search is the adversarial condition for it,
handing the matcher the most freedom to find a spurious consensus. Narrowing it to the shipped
+/-0.25% takes it from 45 s to 3 s and makes the pass mean strictly less. Its time came back instead
from the two things that were never the assertion: each panel's field was being re-projected out of
78k catalog stars once per PARTNER rather than once, and the pairs are independent pure computations
over frozen data, so they run under a bounded `Parallel.For` with the assertions replayed serially
afterwards -- which also reports EVERY false lock rather than whichever one a worker threw from first.
5.1x, same claim.

Star lists because the dense-field failure was purely geometric -- reproducing it needs the positions
and the DENSITY, not the pixels, and 96 frames of FITS is ~9 GB against 2 MiB of lists.

**Three of the four bugs this data set found would have passed a synthetic suite**, because a
synthetic field is built from a transform the test already knows: the origin-convention bias below,
the SIP fit's reference-pixel mismatch, and the seed's anchor pool being diluted by undetectable
off-frame stars. What it covers that synthetic fields cannot: ~4,000 catalog stars per 5-degree frame,
a bright end scrambled by saturation, mount hints wrong by up to 40 arcmin, a meridian flip
mid-mosaic, and 106 overlapping / 272 disjoint panel pairs (the disjoint ones being the
dense-unrelated-field negative case at real density; none of them lock).

### A solver-built WCS answers in DETECTED-CENTROID coordinates

**Never subtract 1 from `SkyToPixel`.** `AttachCDMatrix` derives the CD matrix from the affine that
maps projected pixels onto detected centroids, and re-derives CRVAL per iteration as the sky at the
frame-centre pixel in that same space, so the emitted WCS is self-consistent with the centroids and
needs no 1-based-to-0-based conversion.

Applying one (the plausible-looking `px.X - 1.0`) injects a constant (+0.91, +0.89) px bias --
measured over 1,209 mutual matches on Vela panel 3 and 1,225 on panel 11, where a shift sweep put the
mean residual at (-0.07, -0.10) px unshifted and growing monotonically with any shift. It had cost the
acceptance gate 1.27 px of its 3 px tolerance, and `ReProjectionError` the sharpness of the parity
comparison it exists to make.

### The header hint: `OBJCTRA`/`OBJCTDEC` first, and `RA`/`DEC` is NOT the frame centre

`RA`/`DEC` is the position the *mount reported*; `OBJCTRA`/`OBJCTDEC` is the target the framing put on
the sensor. They agree only on a synced mount, nothing in the header says whether it was, and only the
second one describes the frame.

Why it is load-bearing: the pair-lock anchor pool is the brightest catalog stars that *project inside
the frame from the hint*, so a hint off by most of a field fills the pool with stars the image does not
contain and the seed never reaches consensus. Measured on an SMC integration whose mount was unsynced
by 2.4 deg -- `RA`/`DEC` gave 11-13 hits of 160 against a threshold of 24 (chance 0.9) and fell through
to ASTAP, and widening the search radius to 8 deg did not help because coverage was never the problem;
`OBJCTRA`/`OBJCTDEC` locked at 104/160 and passed the acceptance gate 116/120. TianWen writes both
keywords from the same `ImageMeta.TargetRA/TargetDec`, so the order is invisible on our own files.
