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
`--quad-stars` knob). It has never been pointed at the catalog side.

## Phases

Ordered by return per unit of risk; each independently shippable and measurable.

| # | change | saves | costs accuracy? | risk |
|---|---|---|---|---|
| A | Cancel the losing parity (916k wasted hypotheses) -- **SHIPPED** | ~200 ms | nothing | low |
| B | ~~Tycho-2 pre-baked region index~~ -- **OBSOLETE: already done, worth 0.3 ms** | ~~500 ms~~ 0 | n/a | n/a |
| C | Quad-descriptor matching, reusing `FrameRegistration`'s existing matcher | ~300 ms | nothing, and it *removes* our reliance on `FOCALLEN` | medium |
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

**And that makes the other half of this phase pointless, which is worth stating rather than doing.**
The per-record `EncodeTyc2CatalogIndex` -> base91 string -> `AbbreviationToCatalogIndex` round trip is
still there, still ~131 ms, and now saves NOTHING: the join waits 0.0 ms, so those 131 ms run entirely
in the shadow of the ~198 ms of main-thread work (ngc-csv + simbad + shapes + predefined) that precedes
the await. Removing the string round trip would optimise work that no longer blocks anything. Leave it
until the main-thread phases shrink below it; the numbers above are how you would know.

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
