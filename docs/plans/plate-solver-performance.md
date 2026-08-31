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

| # | change | saves | costs accuracy? | risk |
|---|---|---|---|---|
| A | Cancel the losing parity (916k wasted hypotheses) -- **SHIPPED** | ~200 ms | nothing | low |
| B | Tycho-2 pre-baked region index -- this is TODO 2C, now justified | ~500 ms | nothing | low |
| C | Quad-descriptor matching, reusing `FrameRegistration`'s existing matcher | ~300 ms | nothing, and it *removes* our reliance on `FOCALLEN` | medium |
| D | Cap the star list to ~500 -> yes; bin -> **no** | ~60 ms | binning does, so it is gated on measured FWHM and default off | low |

Target after A-C: **~260 ms**, against ASTAP's 162 ms. D is optional and mostly will not apply
(see below), so it is not counted on.

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
the header scale -- right independently of speed, since `FOCALLEN` was 1.2% wrong on this rig and is
only ever a hint. Worth noting WHICH side was wrong: the header said 205 mm because that was typed
into the profile by mistake, while the optics are 202.5 mm and the solver recovered 202.4 mm from
the stars alone. So the header scale is unreliable not because solving is hard but because nothing
validates what a human entered, which is the argument for not depending on it.

Keep the pair-RANSAC seed: it is what makes dense fields work (see the Vela notes) and it is cheap
once phase A stops paying for its losing parity. The quad path replaces the REFINEMENT loop, not
the seed.

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

## The frozen Vela star lists, and why they are lists rather than frames

`TianWen.Lib.Tests/Data/vela-mosaic-starlists.json.gz` (2.1 MiB) holds STAR LISTS -- not FITS -- from
24 real Vela mosaic pointings / 96 frames / 78k catalog stars: per-frame detected centroids + the
gate-verified WCS (incl. SIP) as an oracle, plus one mosaic-wide catalog so a catalog index is the same
physical star in every panel. `VelaMosaicFieldTests` drives `CatalogPlateSolver.TrySeedPairLock` and
`PairRansacLock` over them; `VelaMosaicStarListExport` (env-gated, needs the user's archive)
regenerates the file.

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
