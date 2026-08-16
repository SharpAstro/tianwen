# The OSC Narrowband Denoiser: state after v17, and what is left

**Status: v17 family complete (9 checkpoints, 3 arms). The fabrication question is CLOSED. The red
PSF question is CLOSED as physics. The blocking discovery is that the training pool is almost
entirely OSC NARROWBAND and nothing in the pipeline knows it.**

Written 2026-08-15 from one long session. Companion to
[ai-denoise-deconv.md](ai-denoise-deconv.md) (the P0-P5 programme),
[filter-inference.md](filter-inference.md) (how the archive's filters were established), and
[narrowband-colour.md](narrowband-colour.md) (the colour half of the same shift). Run records and
checkpoints live under `D:\Astro-Dataset\n2n-smoke\v17`, `v17b`, `v17c`.

## 0. The finding that reframes the rest

Joining the baked dataset's 67 sessions against the locked filter labels in
`D:\Astro-Organized\_provenance\group-{a,b,c}-locked.csv`:

| filter | sessions |
|---|---|
| Optolong L-Ultimate 3 nm | **39** |
| Optolong L-Quad Enhance | **9** |
| broadband | **0** |
| no locked label | 19 (mostly the parked ASI585 group, the 2 mono sets, a few unmeasured) |

**Not one labelled broadband session.** Every conclusion drawn about "the denoiser" so far was
measured on dual-band and quad-band narrowband data, while the recorded deployment target in
[ai-denoise-deconv.md](ai-denoise-deconv.md) says OSC broadband. That is not a small mismatch: a
3 nm dual-band frame has flux in one channel and near-nothing in the others, which is precisely the
regime section 4 of [filter-inference.md](filter-inference.md) records as breaking channel-lock and
star-colour methods.

**Nothing in the dataset path knows the filter, and that is entirely a wrong-root mistake.** The bake
reads `D:\Astro-Pics\{2025,2026,Vela SNR Moasic Project}` (see `bake-provenance.json`), the ORIGINAL
untagged archive, so all 67 retained masters carry `FILTER = 'None'`.

**The tagged archive already exists and was built for exactly this.** `D:\Astro-Organized` holds
6,438 lights whose headers carry `FILTER = 'Optolong L-Ultimate 3nm'` or `'Optolong L-Quad Enhance'`
(the 4,724 cards `dataset tag-filter` wrote during the verified reorganisation), filed
`lights/<camera>/<filter>/<target>/`, with 1,496 calibration frames keyed
`calibration/<CAMERA>/<TYPE>/<date>-g<gain>-o<offset>-t<temp>[-e<exp>]` and 1,506 flats under
`flats/<camera>/<filter>/<date>/`. Nothing needed to be inferred, joined, or heuristically guessed;
the bake simply has to be pointed at it. **No code change is required for the filter to flow**:
`Image.Fits` reads `FILTER` + `FILTCLAS` (preferring the latter, with the blank guard that fix
required) and the writer emits both, so a master built from tagged lights carries the filter forward
by itself. A single `FILTER` card is the correct model for OSC, where one physical filter covers all
three channels; per-channel `FILTERn` is only meaningful for a mono composite, which is task #37.

Cost of the switch: the organized set is 6,438 lights against the 8,290 the untagged bake saw, since
it covers groups A/B/C only and excludes the parked ASI585 group, the two poor mono sets, and the
unmeasured remainder. Those are precisely the sessions that cannot be characterised anyway.

**Point the bake at the three SUBTREES, never at the organized root.** Measured, because rooting at
`D:\Astro-Organized` discovers ZERO sessions while reporting a healthy-looking scan:

```
--archive-root "D:\Astro-Organized\lights" "D:\Astro-Organized\calibration" "D:\Astro-Organized\flats"
```

Two independent reasons, and both are silent:

- **`lights` is in `SessionDiscovery.FrameDirNames`**, and a frame-type directory sitting directly
  under an archive root is by definition a shared calibration library rather than a session, so the
  entire tree is path-excluded. The counter says `6438 excluded-path`, which reads like a filter the
  operator asked for rather than a structural refusal.
- **`targets/` is a junction farm**, a second navigation view whose `<target>/<filter>/<date>` leaves
  are Junctions into `lights/...`. .NET enumeration follows them, so every light is scanned twice
  (6,438 x 2 + 3,002 calibration + 14 provenance = the 15,892 the log reported against 9,454 files
  actually on disk) and one copy is then dropped as a duplicate. `find` does not follow junctions by
  default, which is why a shell count disagrees with the scanner and looks like the scanner is wrong.

Rooted correctly the same archive gives **52 sessions / 6,437 lights, zero exclusions, zero
duplicates**, and the session key carries the filter
(`...|ZWO ASI533MC Pro|HD 71272|Optolong L-Ultimate 3nm`), which is task #19 resolved by data rather
than by code. Multi-target nights split correctly too (2026-01-23 becomes HD 71272 / RCW 27 /
Vela SNR).

## 1. What was established this session

### 1a. Invention is ZERO. The fabrication metric was measuring its own threshold.

`n2n_gate.spurious_per_tile` sets its detection bar at `med + 5*MAD` **of the array being counted**,
so a model that crushes the background lowers its own bar and re-detects speckles the input already
carried. Adding an absolute-bar twin (`ref_mad=` the INPUT's per-tile MAD, so one physical threshold
spans input and output) settles it. Over all 9 checkpoints, on the observer session no arm selected
on, swept along a blend-toward-input strength knob:

| bar | v15 | v17b | v17c |
|---|---|---|---|
| relative (the number that drove ten runs) | +11.1 | +38.1 | +10.1 |
| **absolute (input's MAD)** | **-19.0** | **-19.1** | **-19.2** |

The raw sub carries a **20.1/tile floor**. Every checkpoint, every arm, every strength leaves 1 to 3:
the models REMOVE 85-97% of the spurious detections the noisy input already had and add none.

- The relative count stays as a **ranker** (it is sensitive, and residual speckle against a silky
  background is a real cosmetic harm). It must never again be reported as "invented sources".
- The absolute count discriminates nothing (everything passes hugely), so it is a **safety check**,
  not a selection metric.
- Generalises: **a detection count whose threshold is derived from the array being counted measures
  the threshold, not the array.**

### 1b. More sessions made the frontier WORSE, and capacity is not the lever

Three arms, identical by-name-pinned val, verified by the printed floors matching v15 to the decimal:

| arm | capacity | train | seed scores |
|---|---|---|---|
| v15 | base 32, 0.81 M | 8 sessions x 120 cells | 0.814 |
| v17b | base 48, 1.83 M | 60 x 45 | 0.785 / 0.785 / 0.800 |
| v17c | base 32, 0.81 M | 60 x 45 | 0.803 / 0.806 / 0.735 |

Faint amplitude kept at matched noise, on the clean observer session:

| noise kept | v15 (8 sessions) | v17b (60) | v17c (60) |
|---|---|---|---|
| 0.90x | **0.831** | 0.733 | 0.771 |
| 0.85x | **0.727** | 0.669 | 0.690 |

**8 sessions beat 60**, seed-consistent, and base 48 vs base 32 land on the same frontier so capacity
is excluded. The training-log frontier agrees with wider separation and adds that
invention-at-matched-noise is also worse for the 60-session arms.

**Invention has a knee at ~0.80x noise and is ~0 above 0.85x** (relative bar, matched noise: 0-4 at
0.85x, 10 at 0.80x, 18-32 at 0.75x, 62 at 0.70x). So the deployable operating point is a STRENGTH
setting, not a property of a checkpoint. `n2n_v17c_s0_final.pt` sits at 0.80x with +2.0 as trained.

### 1c. Red's wide, field-inverted PSF is acquisition plus optics, not a bug

Autofocus minimises star size where the flux is: green+blue are 75% of an OSC's photosites, and under
a dual-band filter the bright signal is literally the OIII line at 500 nm, which lands in the green
AND blue photosites while Ha at 656 nm reaches only the red 25%. **Red is never what focus was
optimised for.** Over 65 sessions red is the sharpest channel in 6 (9%); green 51%, blue 40%.
red/green centre width 1.381 quad-band, **1.642** 3 nm. Because a curved chromatic focal surface makes
defocus field-dependent, red's defocus can shrink off-axis while green's aberrations grow, which is
the apparent "inversion".

Four code hypotheses falsified, recorded so they are not retried (detail in task #19):

1. Per-channel flux banding (the original diagnosis). Fixed anyway in `069e5b14` and worth keeping,
   since comparing channels measured on different star populations is not a comparison, but it moved
   the ratio only 2-5% and one train the wrong way.
2. Nebulosity inflating the centre. Fields without bright extended emission invert as hard.
3. Estimator censoring at `HalfMaxDiameter`'s `2*rAperture` ceiling. Real mechanism, but red is
   censored 1.2% and GREEN 1.7% (exact-even-integer fingerprint, 0.0% odd-integer control).
4. Moffat beta as a defocus proxy: rho -0.10, though beta pools all radii so this is weak either way.

**An argument that kept this misdiagnosed and must not be reused:** "a star cannot be more elongated
and sharper at once". False. Defocus and astigmatism are independent aberrations and a curved
chromatic focal surface moves them in opposite directions across the field.

**Consequence: the P2 hold on red's radial profile is LIFTED.** It is real chromatic signature, and a
deconvolver that ignores it will be wrong about red on every fast rig.

### 1d. Two calibration fixes landed

- **`c1079f4b`** flats take an exposure-matched dark-flat pedestal, bias as the fallback, with
  mislabeled short darks rescued by a 4x exposure-ratio gate (the archive's dark-flats are written
  `IMAGETYP=DARK`, and the re-bake log shows 68/68 sessions were on bias pedestals with zero
  DARKFLAT-labeled frames).
- **`ad08d4fc`** ranks pedestal candidates by the error they LEAVE, `|t_c * 2^(dT/6) - t_f|` in
  seconds of the flat's own thermal signal, instead of adding degrees to seconds with an arbitrary
  weight. A bias scores exactly `t_flat`, so the preference order falls out of the physics including
  where it inverts (one doubling, ~6 C). A light-dark is never scaled onto a flat: it fails the gate
  and is not a candidate.

### 1e. The field-radius profile now samples one star set

**`069e5b14`**: bins come from stars matched across every channel by centroid (1 px radius against a
0.064 px median inter-channel shift), banded on a single reference flux (green on a 3-channel
master). Records carry `RadiusSampling = "common-stars"`; all 67 sessions re-measured via
`--force-psf` from retained masters in about 6 minutes, tiles untouched.

### 1f. N1 landed, and the pool is 100 percent narrowband with nothing broadband in it

`D:\Astro-Dataset\2025-2026-organized`, baked 2026-08-15 in 231 min: 51 of 52 sessions, 5,908
registered subs, 159,300 tiles, 0 failed, 0 skipped-no-dark, parity OK, 7 test sessions pinned.

**The filter is now read from headers for every session, and the answer is total:**

| Filter | Sessions |
|---|---|
| Optolong L-Ultimate 3nm | 40 |
| Optolong L-Quad Enhance | 11 |
| broadband | **0** |

Against 48 of 67 labelled by the old side-table join. Section 0's finding is no longer a sample,
it is the whole pool: **there is no broadband session in the training set at all.**

**The pool got smaller and cleaner, 68 sessions to 51.** The 17 that fell away are the ones the
old bake could not label, because they are the groups the reorganisation has not reached (the
parked ASI585 set, the mono ASI1600MM sets, group D and beyond). They are now absent rather than
present-and-unlabelled, which answers the second open question below by dissolving it. Given 1b,
a smaller pool is not on its face a loss.

**Two facts that change the shape of N5.** The filter split is severely unbalanced per train:
SV605CC is 10 quad-band against 4 3 nm, but ASI533 is 36 3 nm against **one** quad-band. So keying
`FieldRadiusProfiles` by (train, filter) creates a single-session cell, and N5 has to say what that
cell does (fall back to the train profile, or be withheld) rather than just changing the key. And
the master integrator is MIXED *within* each filter (3 nm: 33 BayerDrizzle / 7 Float16Staged), so
the grouping error the report already warns about for `MasterStrategy` is not resolved by adding
the filter.

**Red's inversion reproduced on independently organized data**, both trains, which is worth
recording because 1c closed on the old bake: ch0 falls centre to corner (2.815 to 2.235 px on
SV605CC, 2.762 to 2.399 on ASI533) while ch1 and ch2 rise together over the same bins.

**The one skip is HIP 42861 / 2025-12-28**, `fewer-than-2-registered`, 48 of 49 subs failing quad
fit against a healthy census (67 median stars, HFD 1.89, ecc 0.46). Known, and now a fixture in
`stats/skipped-sessions.jsonl` rather than a rediscovery.

**What this run could NOT answer, which is the more useful result.** The dark-flat pedestal path
(`c1079f4b`, `ad08d4fc`) fired here for the first time, because the reorganisation files
calibration on `(date, gain, offset, exposure)` and so separates the dark-flats that used to hide
inside folders named `DARK`. Whether any flat actually took a dark-flat pedestal is **not
recoverable from any artifact**: not the master header, not `psf-sessions.jsonl`, not
`session-timings.jsonl`, not `tiles-manifest.jsonl`. A behaviour change shipped and cannot be
observed. Task #39, and the same class as the timing store that persisted the figures it existed
to derive (`8ff20d36`).

Two smaller defects found in the report itself, both in #39: the noise-floor summary table
formats at three decimals over values of ~4e-5 and so prints `0.000` across every percentile, and
`FILTCLAS` is written as the literal `'Unknown'` on every master while `FILTER` carries the real
name.

### 1g. The filter hypothesis is refuted, and the arms differ by a CAMERA instead

`n2n_arm_filter_map.py` joins the v15 and v17 arm membership onto the organized bake and, where
that cannot reach, onto the 58 measured verdicts in `_provenance`. Output:
`D:\Astro-Dataset\n2n-smoke\arm-filter-mapping.csv`, one row per (arm, session) with its resolution
basis. Arms recovered from the training caches: v15 is `C:\tianwen-scratch\n2n-ds` (8 train), v17 is
`n2n-big` (60 train), and both pin the SAME 2 val sessions, so they are comparable by construction.

| Arm | Resolved | 3 nm | Quad | Homogeneity | Worst case |
|---|---|---|---|---|---|
| v15 train (8) | 8 of 8 | 6 | 2 | **75%** | exact, nothing unresolved |
| v17 train (60) | 48 of 60 | 39 | 9 | **81%** | 65% if all 12 unresolved were quad |

**The hypothesis needed v15 homogeneous and v17 mixed, and the arms are not distinguishable on this
axis at all.** Do NOT read the 75-versus-81 as a reversal: v15's figure rests on 2 quad-band
sessions out of 8, whose 95% interval spans roughly 3% to 65% and so covers v17's mix comfortably.
The direction is sampling noise. What survives is only the negative: **there is no evidence of a
filter difference between the arms**, so the filter cannot carry 1b, and one more sub-hypothesis is
closed rather than a new one opened.

**What the mapping found instead, which is a better hypothesis than either filter or PSF:**

| Arm | ASI533MC Pro | SV605CC | **ASI585MC Pro** |
|---|---|---|---|
| v15 train (8) | 5 | 3 | **0** |
| v17 train (60) | 41 | 11 | **8** |

**v17 trains on a camera v15 never saw, and it is the one body whose ADU scale is documented as
unresolved** ([filter-inference.md](filter-inference.md) section 8 parks the ASI585 group for
exactly that reason: "this body's ADU scale is unresolved, so its sky rate cannot be trusted"). All
8 are also unresolved for filter here, because they are in no organized bake and were never
measured. That matters more than it sounds: the denoiser is **conditioned on noise sigma**, sigma
comes from the normalised pixel scale, and an unresolved ADU scale means those tiles carry a
MISLABELLED conditioning input rather than merely a noisy one. 13% of the pool teaching the
conditioning axis the wrong thing is a specific mechanism for a worse frontier, where "more
heterogeneous data" was only ever a description of one.

It is also the cheapest thing left to test: drop 8 sessions, retrain, no code change. **Do that
before N2 spends GPU time on PSF conditioning**, because if it explains the regression then N2 is
answering a question that was not being asked.

Two caveats to hold. The ASI585 confound and the PSF-heterogeneity hypothesis are not exclusive,
and dropping the 8 also shrinks the pool 60 to 52, so the arm needs a size-matched control to avoid
re-running 1b's own confound in a new costume.

### 1h. The ASI585 sessions are innocent, and the arms were never a clean session-count contrast

v18 (`D:\Astro-Dataset\n2n-smoke\v18\README.md`, `bars-v18.txt`). Two 52-session arms, both strict
subsets of v17's own 60, differing by exactly 8 each way; v17c's config verbatim, three seeds each.
Faint amplitude kept at matched noise 0.90 on the observer session:

| Arm | seeds | mean |
|---|---|---|
| v15 (8 sessions) | 0.821 0.823 0.850 | **0.831** |
| v17c (60) | - 0.730 0.812 | 0.771 |
| v18a-no585 (52) | 0.681 0.810 0.805 | 0.765 |
| v18b-control (52) | - 0.754 0.749 | 0.752 |

**A versus B is 0.013 apart against a 0.13 within-arm seed spread in A.** Not a difference. The
ASI585's unresolved ADU scale is not what hurt v17, so 1g's mechanism is dead. **And dropping 8
sessions of any kind did nothing either**: both 52-arms land on v17c, not on v15, so the 60-to-52
step is invisible while the 8-to-52 gap is intact.

**What the run exposed is that v15 and v17 were never a clean session-count contrast:**

| Arm | sessions | cells per session | train cells |
|---|---|---|---|
| v15 | 8 | 120 | **960** |
| v17 | 60 | 45 | 2700 |

**v15 trains on about a third of the data and wins.** So every "8 sessions beat 60" statement,
1b included, has been carrying two changes at once: fewer sessions AND 2.7x the sampling density
within each. v18 has now flattened the session-count axis between 52 and 60, which leaves volume
and density as the untested half and makes N2c the next run.

### 1i. It is the session COUNT, it saturates by 21, and one observer cannot prove that

v19 (`D:\Astro-Dataset\n2n-smoke\v19\README.md`, `bars-v19.txt`). Two arms complete a 2x2 against
v15 and v17c; all four nested subsets of v17's 60, same pinned val, same observer cells, three
seeds each. Faint amplitude kept at matched noise:

| Arm | sessions | cells/session | train cells | epochs | 0.90 | 0.85 | 0.80 |
|---|---|---|---|---|---|---|---|
| **v19d** | **8** | 45 | 360 | 89 | **0.842** | **0.747** | **0.645** |
| v15 | 8 | 120 | 960 | 33 | 0.831 | 0.727 | 0.609 |
| v19c | 21 | 45 | 945 | 34 | 0.770 | 0.643 | - |
| v17c | 60 | 45 | 2700 | 12 | 0.771 | 0.690 | - |

**360 cells from 8 sessions beats 2700 from 60**, and it closes in three comparisons. v19d vs v15
holds the sessions fixed while density, volume and epochs each swing 2.7x, and nothing moves, so
**all three are irrelevant**. v15 vs v19c matches volume (960/945) and epochs (33/34) and does
move, so with density already excluded the only thing left is **count, 8 against 21**. v19c vs
v17c is flat, so the effect **saturates by 21** and v18's blindness between 52 and 60 was expected.
The groups separate cleanly, not on means: few-session arms span 0.821-0.866 over six seeds,
many-session arms 0.730-0.812 over five, no overlap.

**This raises task #36 from one hypothesis among four to the only survivor.** The mechanism must
scale with the NUMBER of distinct sessions while ignoring how much data each brings, and "one
scalar sigma cannot describe N incompatible regimes, so the model averages a prior that fits none"
is that shape. It also predicts the saturation: eight regimes already exhaust one scalar.

**And the result does not license training on 8 sessions.** Every number here is one observer
session. Few-session arms winning ON RIM NEBULA is equally consistent with those 8 sitting closer
to Rim Nebula than a broad pool's average does, which is the ordinary narrow-training-set story and
would reverse on a different field. The table cannot separate the two, because the observer never
moves. **So N2d rotates the observer before anything else is concluded from 1b, 1h or 1i** -- it
needs no training, only evaluation, and if proximity is what is being measured then the whole
8-beats-60 line has been steering this programme since v17 on a sampling artifact.

### 1j. The observer rotation clears it: proximity is not what produced 1i

v20 (`D:\Astro-Dataset\n2n-smoke\v20\README.md`, `rotate-v20.txt`). No training; the same 12
checkpoints re-scored on every session no arm has seen, each with its own raw-sub floor and noise
normalisation. Faint amplitude kept, few-session arms against many:

| Observer | v15 | v17c | v19c | v19d | few - many |
|---|---|---|---|---|---|
| ASI533 / Rim Nebula (0.90) | 0.816 | 0.781 | 0.730 | **0.825** | +0.035 |
| SV605CC / Horsehead | 0.891 | 0.858 | 0.886 | **0.901** | +0.005 |
| SV605CC / Skull and Crossbones | 0.863 | 0.826 | 0.839 | **0.865** | +0.024 |
| ASI585 / 24mm wide field | 0.858 | 0.851 | 0.846 | **0.856** | +0.005 |

**8 of 8 cells across four observers and two noise levels favour the 8-session arms**, and v19d
(360 cells) is best in 7 of 8. The direction never flips, which is exactly what the proximity
story required.

**The decisive cell is the ASI585 field.** The few-session arms contain no ASI585 session at all
(v15's eight are 5 ASI533 + 3 SV605CC) while v17c trained on eight. On the one observer where the
many-session arm has home advantage and the few-session arms are strangers, the few-session arms
still win. Proximity cannot produce that.

Read it with two limits. **Two of the four margins are 0.005, below seed spread, and are ties**;
the evidence is the consistency of a direction, not the size of any cell. And **a residual
proximity signal is visible** -- the largest margin is on Rim Nebula, the smallest on the two
fields furthest from the pool -- so proximity likely contributes to the v19 numbers without
producing them.

**Consequence: task #36 is unblocked** and the averaged-prior mechanism is the one to attack. Also
worth noting the ceiling: only four observers exist because v17 consumed 62 of the root's 67
sessions and two of the rest are mono. Widening this needs more of the archive baked (task #11),
not more evaluation code.

### 1k. Band conditioning does not rescue the 60-session arm, and the cheap proxy is spent

v21 (`D:\Astro-Dataset\n2n-smoke\v21\README.md`). `--cond-bands` (3 DoG-band planes) against
`--cond` (1 scalar) on the same two caches, config otherwise v17c's, three seeds, four observers.

**The shape of #36 had to change before it could run.** Conditioning planes are computed FROM THE
TILE inside `with_sigma()`, at training and inference alike. A per-session PSF width read out of
`psf-sessions.jsonl` would train the model to lean on a number a deployed denoiser can never
obtain, which is [the tile-border train/inference asymmetry](../../docs/plans/ai-denoise-deconv.md)
in another costume. `--cond-bands` is the conditioning upgrade that respects the constraint, and
`run-v17c.ps1` had pre-registered this exact re-test (v14 rejected band conditioning, but on the
8-session data "where a shape descriptor had far less to do").

Faint amplitude at matched noise 0.90, usable seeds in brackets:

| Observer | 60 scalar | 60 bands | 8 scalar | 8 bands |
|---|---|---|---|---|
| Rim Nebula | 0.781 (1) | **never reaches 0.90** | 0.825 (3) | 0.809 (3) |
| Horsehead | 0.858 (3) | 0.902 (2) | 0.901 (3) | 0.899 (3) |
| Skull and Crossbones | 0.826 (3) | 0.844 (2) | 0.865 (2) | 0.908 (1) |
| ASI585 wide field | 0.851 (2) | 0.847 (2) | 0.856 (2) | 0.906 (3) |

**No.** The 60-band arm beats 60-scalar on two of three observers where it produces a number, still
trails the 8-session arms on two of three, and on Rim Nebula cannot be pushed to 0.90 at all. **It
also destabilised training**: `v21a_s0` plateaued at 0.94x and never passed the gate, so no
`_final.pt` exists, which no scalar-conditioned arm here has ever done.

**Read it with the seed counts.** They run 1, 2 and 3 across cells, so several means come from very
small and unequal subsets (`8 bands` on Skull is ONE seed). The supportable claim is "band
conditioning did not rescue the 60-session arm and made training less reliable", NOT a ranking of
the four arms. **We are at the resolution limit of 4 observers x 3 seeds with a gate that sometimes
yields nothing**; a finer question needs more observers (task #11) or more seeds first.

**What survives.** The 3 bands measure per-band noise SIGMA, i.e. the noise's colour. 1i's
hypothesis is about PSF regimes, and PSF width is a property of the SIGNAL that no band-sigma plane
reports. So this kills the cheap proxy, not the hypothesis. Testing it properly needs a
signal-scale plane measurable from one tile (autocorrelation width of the high-passed image, or a
star-size estimate), which is new code and a real estimator-design problem.

**Given v19d already beats every other arm on every observer, N4 comes first.** Ship the model that
exists, then decide whether the estimator is worth building.

### 1l. Why 8 beats 60, measured on the checkpoints: the N2N premise is broken, and many sessions generalise the damage

v22 (`D:\Astro-Dataset\n2n-smoke\v22\README.md`). Four measurement scripts, ZERO training, all
pre-registered in their docstrings before the first number. This is the mechanism 1i asked for
and 1k could not reach, and it is not a conditioning problem at all.

- **Every training pair is two subs of the same session, and the N2N independence premise is
  violated in every session, not a bad subset.** Time-adjacent sub pairs' residuals (vs master,
  high-passed, faint-masked) correlate 1.5-3x more than distant pairs' on essentially all 65
  measurable sessions (adjacency excess, median +0.016). Seeing bursts, drift, walking pattern:
  target corruption that the training loss REWARDS keeping. The pre-registered "v19d's 8 sit
  clean" prediction FAILED (armD's Statue session ranks 3rd of 65), which is what killed the
  simple violator-inclusion story and forced the sharper one.
- **The arms differ in how they TRANSFER.** On the four observers (nobody trained on them), all
  arms keep the pair-shared residue preferentially (0.63-0.72 kept, against 0.24-0.34 of the
  white part) -- but the many-session models keep more of it (0.71-0.72 vs v19d's 0.63, seeds
  non-overlapping) and more of everything (kept_total 0.33 vs 0.24). On their OWN train sessions
  the arms are near-equal. So: **few sessions = memorise specific residue patterns that do not
  transfer = strip hard and uniformly off-pool; many sessions = generalise "structured
  high-frequency content is often real, keep it" = a soggier operator on every unseen session.**
  21 = 60 to three decimals (0.716 vs 0.710), the saturation visible in the operator itself, and
  the long-logged "same weights read 0.04-0.17 higher noise on a second session" shift is this
  sogginess by another name. Conditioning cannot fix it because the corruption is in the TARGETS,
  which is why 1k had to come out negative.
- **The star-only gate's blind spot was checked and cleared.** armD is mostly sparse star fields
  and nothing since v15 ever measured extended structure, so v19d could have been winning the
  faint-star metric by ironing nebulosity flat. Raw-operator measurement showed exactly that
  deficit (4-16 px band, all four observers) -- and at MATCHED NOISE it vanishes: v19d holds
  nebulosity at parity (0.97-1.00, every arm) while keeping MORE 1.5-4 px fine structure and more
  faint-star amplitude, at 0.90 and 0.85 both. The amp column reproduces 1j's numbers exactly.
  **v19d dominates end to end; N4 ships it with its one untested axis now tested.**
- **What is measured is not yet proven causal.** v23 (`D:\Astro-Dataset\n2n-smoke\v23\`, written
  and pre-registered, NOT yet run) is the causal test: `--pair-time far` trains the 60-arm only
  on time-separated pairs (prediction PA: recovers toward v19d -- and if so, far-pairing is the
  one-flag fix that finally lets more data help), `near` is the dose control (PB: degrades), and
  armE is 8 DIFFERENT sessions (PC: lands in the few-session band, so the effect never needed
  armD's particular 8).

## 2. Traps this session re-tripped, which are already documented elsewhere

Recorded here because each one cost real time and each was written down BEFORE it was hit.

- **`RGB` and `LUM` are PROCESSING MODES, not filters, on an OSC sensor.**
  [filter-inference.md](filter-inference.md) section 1 says so and records that treating those tokens
  as filter evidence put 4,533 frames of false evidence into a first pass. A paired comparison in this
  session was reported as "broadband vs narrowband" when `2025-10/Orion/RGB` is
  **Optolong L-Quad Enhance** by measurement (`group-c-locked.csv`: B/G 1.097, and the basis text
  names the trap explicitly). The comparison was really quad-band vs 3 nm dual-band, which still
  supports the chromatic reading and is in fact stronger evidence, but the label was wrong.
- **The filter-inference work exists and was already run.** `dataset tag-filter` plus
  `FitsHeaderEditor` are committed; the inference is validated over 58 sessions / 8,507 frames across
  two bodies; the verdicts are locked per session with their basis. It was rediscovered from scratch
  in this session as though it were an open question.
- **Grep the notes before calling a measurement a finding.** Same lesson as the HIP 42861 anomaly,
  which was re-"discovered" three times.

## 3. What is left

Ordered by value per unit of work, not by dependency.

| Phase | Deliverable | Cost | Why now |
|---|---|---|---|
| ~~**N1**~~ | ~~**Re-bake from `D:\Astro-Organized`.**~~ **DONE 2026-08-15**, see 1f. `D:\Astro-Dataset\2025-2026-organized`, 51/52 sessions, 159,300 tiles, 231 min. Every session carries its filter from the header. | 3.9 h, no code | Everything below depended on the dataset knowing its filter, and the work to make that true was already done and then not used. |
| ~~**N2a**~~ | ~~**Retrain v17 without the 8 ASI585 sessions.**~~ **DONE 2026-08-16, negative on both counts, see 1h.** The ASI585 sessions are innocent and dropping 8 sessions of any kind changes nothing. | 1.6 h | Ruled out the cheapest mechanism, and exposed the axis nobody had separated. |
| ~~**N2c**~~ | ~~**21 sessions x 45 cells.**~~ **DONE 2026-08-16, see 1i.** Ran as a 2x2 with an 8x45 arm rather than alone, which is what made it decisive: session count is the lever, volume / density / epochs are not, and it saturates by 21. | 1.4 h | Answered, and it promoted #36 from one candidate to the only one. |
| ~~**N2d**~~ | ~~**Rotate the observer session.**~~ **DONE 2026-08-16, see 1j.** 8 of 8 cells favour the few-session arms, including on the ASI585 field they never trained on and v17c did. Proximity excluded. | 25 min | Cleared the confound that would have invalidated 1b, 1h and 1i, and unblocked N2b. |
| ~~**N2b**~~ | ~~**PSF conditioning in the trainer.**~~ **PARTLY DONE 2026-08-16 as band conditioning, negative, see 1k.** The cheap tile-measurable proxy (`--cond-bands`, 3 noise-colour planes) does not rescue the 60-session arm and destabilised training. | 1.4 h | Killed the proxy, not the hypothesis: band sigma describes NOISE colour, and 1i is about SIGNAL scale. |
| ~~**N2e**~~ | ~~**A signal-scale conditioning plane, measurable from ONE tile.**~~ **MOOT per 1l (2026-08-16).** The mechanism is corruption of the TARGETS (time-correlated pair residue), which no input-side plane can describe away; 1k's negative was structural, not a proxy problem. The fix axis is N8's pair selection, not a richer estimator. | - | Withdrawn before any code was written, which is the cheap time to withdraw it. |
| **N3** | **Restate the deployment target.** The pool is 3 nm + quad-band + zero broadband. Either accept OSC narrowband as the target (and say so everywhere the docs claim broadband), or deliberately acquire broadband training data. | doc | Section 0. Everything measured so far is a narrowband result wearing a general label. |
| **N4** | **Ship a checkpoint behind a strength dial. The one to ship is now `n2n_v19d_s*_final.pt`** (8 sessions, 360 train cells, scalar conditioning), which is best or tied-best on all four observers at both noise levels and beats the v17c checkpoint the earlier draft of this row named. Wire as an `IDenoiseEnhancer` in the SAS tier with strength exposed; `with_sigma`'s `strength` argument IS the dial, no retraining needed. | medium | **This is now the next thing to do**, and 1l closed its last open risk: at matched noise v19d holds nebulosity at parity and leads on fine structure, so the star-only gate was not hiding a trade. If N8's PA holds, a "60 far" model may later supersede it behind the same interface. |
| **N8** | **Run v23: the causal test + the which-8 arm** (`D:\Astro-Dataset\n2n-smoke\v23\`, fully written and pre-registered; `run-v23.ps1` then `n2n_rotate.py`). 60-arm on time-FAR pairs only, on time-NEAR pairs only, and armE's 8 different sessions; predictions PA/PB/PC in the run script. | 9 trainings, ~2-3 h GPU | Proves or kills 1l's causal reading, and PA holding turns into the fix that lets a large pool finally beat v19d. Independent of N4; run it whenever the GPU is idle. |
| **N5** | **Key `FieldRadiusProfiles` by (train, filter).** N1 has made the filter available from headers, so the key change itself is a few lines. It is NOT only a key change: per 1f the split is 36-to-1 on the ASI533, so the design decision is what a one-session cell does (fall back to the train profile, or be withheld as unsupported), and that has to be stated rather than emerge. Task #19. | small | Before N1 it needed a side-table that should never have been contemplated. |
| **N6** | **Mono narrowband support.** Deferred until good mono data exists; the archive's 2 ASI1600MM sets are assessed as poor and must NOT be used as a baseline. Task #37. |  | The user intends to shoot true narrowband, where per-filter focus removes 1c's root cause at acquisition. |
| **N7** | Red centre-vs-corner star cutouts, if the optical reading ever needs pixel-level confirmation rather than statistical. | small | Optional. The four falsified hypotheses plus train- and filter-dependence already carry it. |

### Open questions worth stating

- ~~**Is the v17 regression a filter effect after all?**~~ **CLOSED, see 1g.** The mapping is built
  (`D:\Astro-Dataset\n2n-smoke\arm-filter-mapping.csv`) and the arms are not distinguishable by
  filter mix, so the filter cannot carry 1b. That is a negative result, not a reversal: v15's 8
  sessions are too few to rank against v17's 48.
- ~~**What are the 19 unlabelled sessions?**~~ **Dissolved by 1f.** They are the groups the
  reorganisation has not reached, and the organized bake simply does not contain them: the pool went
  68 sessions to 51, all labelled. The heterogeneity question they posed is now a question about
  whether losing 17 sessions costs anything, which 1b suggests it does not.
- ~~**Does PSF conditioning subsume filter conditioning?**~~ **Dissolved by 1l.** The 8-vs-60
  mechanism is target corruption, not an input-description gap, so neither PSF nor filter needs to
  be a conditioning input for THIS question. The filter stays a grouping key for analysis.

## 4. Invariants

- **Never write to `D:\Astro-Pics`.** The reorganisation model (copy to a new root, verify both sides,
  leave the original untouched) is not a safety dance to be optimised away.
- **A filter label is measured evidence or it is a guess, and the two must be distinguishable in
  storage.** Folder tokens are not evidence; `RGB` and `LUM` are actively misleading.
- **Never compare fabrication counts across arms at different noise levels.** Match noise, or use the
  absolute bar, or both.
- **Val is pinned BY NAME** (`--val-from-meta`), and the proof it worked is the printed floors, not
  the config.
- **Do not edit `n2n_smoke.py` or `n2n_gate.py` while a multi-run chain is in flight**; each run
  re-reads them.
- Mono sessions stay excluded (`drop_foreign_channel_sessions`) for two independent reasons now:
  confound control, and the existing mono data being poor.

## 5. Artifacts

- Run records: `D:\Astro-Dataset\n2n-smoke\v17\README.md` (budget error, bar-migration autopsy,
  spurious-site composition), `v17c\README.md` (the three-arm comparison and the dual-bar result).
- Checkpoints: `C:\tianwen-scratch\n2n-big\n2n_v17{b,c}_s{0,1,2}{,_final}.pt` (`_final` = selected).
- Scripts, in `v17b/scripts` and `v17c/scripts`: `n2n_bars.py` (dual-bar, matched-noise, all 9
  checkpoints), `n2n_gate.py` (with the absolute-bar twin), `n2n_frontier.py`.
- PSF analysis, scratchpad: `psf_radial.py` (re-implements the C# aggregation so a check does not
  depend on the report renderer), `psf_perobject.py`, `psf_homogeneous.py`.
- Pre-fix PSF store, for before/after: `D:\Astro-Dataset\n2n-smoke\psf-banding-before\`.
- **The N1 dataset: `D:\Astro-Dataset\2025-2026-organized`** (tiles, masters, session-masters,
  `tiles-manifest.jsonl`, `test-sessions.txt`, and `stats/` holding `psf-noise-report.md`,
  `psf-sessions.jsonl`, `session-timings.jsonl`, `skipped-sessions.jsonl`). The previous
  `2025-2026-darkscaled` bake is what v15/v17 trained on and is NOT interchangeable with it:
  different session ids, different pool, no filter.
- Folder-vs-header survey: `tools/astro-archive-folder-vs-object.py`, which reads the bake's own
  scan summary rather than walking `D:`, so sizing the task #38 mosaic-panel question costs no disk
  I/O against a running bake.
- **Arm-to-filter mapping: `D:\Astro-Dataset\n2n-smoke\arm-filter-mapping.csv`**, built by
  `n2n-smoke/scripts/n2n_arm_filter_map.py`. One row per (arm, session) carrying the resolved
  filter, its source (organized bake or measured verdict) and its join basis, so a later reader can
  see which attributions are exact and which are not. **The arms live in the training caches, not in
  the run records**: v13/first-8 is `C:\tianwen-scratch\n2n\meta.json`, v15 is `n2n-ds`, v17 is
  `n2n-big`; each holds `train_sessions` + `val_sessions` by name.
- Filter verdicts: `D:\Astro-Organized\_provenance\group-{a,b,c}-locked.csv`, with per-session basis.
