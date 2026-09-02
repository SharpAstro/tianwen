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
  **Fixed 2026-09-02:** every production scan now goes through `FileEnumeration` (`TianWen.Lib/IO`),
  which never lists or enters a reparse point, so the junction farm is invisible to a walk rather
  than doubled. The three-subtree root advice above still stands because of the first reason.

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

### 1i. ~~It is the session COUNT~~ -- SUPERSEDED by 1m: it is armD's particular eight

> **Correction, 2026-08-16 (v23).** The conclusion below is wrong, and the error is in the
> instrument rather than the arithmetic. The 2x2 varied session COUNT and session IDENTITY
> together and attributed the result to count. An 8-session arm built from eight DIFFERENT
> sessions (armE, same camera split, same 360 cells, same config, same pinned val) does not
> reproduce it: 0.726 on Rim Nebula against armD's 0.825, and below every 60-session arm there.
> Read this section as the measurement it is -- armD beats v19c and v17c, repeatedly and on four
> observers -- and not as the count claim it draws. See 1m.

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

> **Still valid, and narrower than it reads (v23).** The rotation genuinely excludes
> observer-proximity: armD wins on four observers including a rig it never trained on. What it
> cannot exclude, because nothing here varies it, is the TRAINING set -- every few-session number
> in this section comes from armD's same eight sessions. That is the criticism this section makes
> of using one observer, one level up. v23 varied it; see 1m.

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

### 1l. The operator difference is real and replicates; ~~the causal story attached to it~~ does not

> **Correction, 2026-08-16 (v23).** The MEASUREMENTS below all stand, and armE independently
> replicates the operator finding. The CAUSAL CHAIN drawn from them -- count -> soggy operator ->
> worse frontier -- breaks at the second arrow: armE has armD's hard operator (kept_shared 0.631,
> kept_total 0.263, both in the few-session band) and still loses on every observer. So the
> operator property is a genuine count effect that is NOT the mechanism of the performance
> difference. The residue pathway is separately closed by v23's pair-time arms. See 1m.

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

### 1m. All three v23 predictions failed, and the count claim goes with them

v23 (`D:\Astro-Dataset\n2n-smoke\v23\README.md`). Nine trainings, three scorers, everything
pre-registered in `run-v23.ps1` and the scorer docstrings before any run.

Faint amplitude at matched noise 0.90, four observers (3/3 seeds unless noted):

| Observer | 60 any | 60 far | 60 near | **8 any D** | 8 any E |
|---|---|---|---|---|---|
| Rim Nebula | 0.781 (1) | 0.747 (2) | 0.746 | **0.825** | 0.726 |
| Horsehead | 0.858 | 0.864 | 0.891 | **0.901** | 0.863 |
| Skull and Crossbones | 0.826 | 0.854 | 0.876 | **0.875** | 0.858 |
| ASI585 wide field | 0.851 (2) | 0.845 | 0.868 | **0.869** | 0.866 |

- **PC failed, and it is the one that matters. Eight different sessions do not reproduce the
  result.** armE matches armD on camera split (5 ASI533 + 3 SV605CC), train cells (360), config,
  seeds and pinned val, and lands 0.726 on Rim against armD's 0.825 -- below every 60-session arm
  there -- while losing to armD on all four observers. **v19's 2x2 varied session count and
  session identity together and concluded count; v20 then rotated the observer but nothing ever
  rotated the TRAINING set.** The counts never ordered anything either: on Rim, 60 (0.781) > 21
  (0.730) > armE (0.726), with armD alone at 0.825.
- **PA and PB failed: pair-time moves nothing.** `far` travelled **-9.9%** of the 60-to-8 gap on
  kept_shared and **-26.8%** on kept_total, i.e. away from v19d; `near` travelled -6.9% and +2.5%.
  All four many-session operators sit within 0.009 on kept_shared (0.710-0.719) whatever their
  pairing. **The correlated-residue pathway is closed** and no further pair-time work is warranted.
- **What survives, and armE is what makes it solid: the OPERATOR property is a real count
  effect.** Two disjoint 8-session sets land at kept_shared 0.626 / 0.631 and kept_total 0.241 /
  0.263; three many-session configurations at 0.710-0.719 and 0.329-0.355, bands non-overlapping.
  Few-session training does produce a harder, less residue-keeping operator. **It is just not what
  wins the frontier**, since armE has it and loses.
- **P10 held**: nebulosity (4-16 px) is 0.973-1.006 at matched noise for every arm, so no arm
  trades it. Fine structure (1.5-4 px) still favours armD alone (0.974 / 1.017 / 1.016 / 0.996
  against armE's 0.909 / 0.981 / 0.979 / 0.982).

**N4 is unaffected: v19d still ships.** Best or tied-best on all four observers, leads fine
structure everywhere, nebulosity at parity. What changes is the explanation and the recipe --
**there is no size recipe to generalise, because someone rebuilding this by picking eight sessions
would most likely get armE.**

**Two things about this experiment to hold against its own conclusions.** `far`'s averaged
regimes used BLOCKED time splits (slots 1-4 vs 5-8), re-tripping a trap this repo already
documents: `SessionRegistrar` interleaves (`i % 2`) precisely because seeing and transparency
drift monotonically, so contiguous halves disagree about the SIGNAL. That handicapped `far` on
~2/3 of its steps and is the likely reason `near` beat it on all four observers, so the frontier
half of PA never got a clean test (the mechanism half did, and neither operator moved at all).
And armE was matched on camera but not CONTENT -- it carries a comet (a moving target, which
corrupts both the pair and the master) and a galaxy where armD has a cluster and two nebulae -- so
**"armD is unusually good" and "armE was a bad draw" are not yet separated**, and the supportable
claim is only that eight is not sufficient and which eight dominates. N9 separates them.

### 1n. A third disjoint eight closes it: no recipe, high variance, and Rim carries the whole story

v24 (`D:\Astro-Dataset\n2n-smoke\v24\README.md`). armF -- eight further sessions, disjoint from
armD and armE, built to armD's recipe (5 ASI533 + 3 SV605CC, 4 HD mosaic panels + an RCW nebula,
8 distinct nights, no moving target, no galaxy) and screened for two contaminations neither earlier
arm was checked for. Predictions PD/PE/PF in `run-v24.ps1`, written before the prepare.

Faint amplitude at matched noise 0.90 (3/3 seeds unless noted):

| Observer | 60 any | 21 any | **8 D** | 8 E | **8 F** |
|---|---|---|---|---|---|
| Rim Nebula | 0.781 (1) | 0.730 | **0.825** | 0.726 | 0.739 |
| Horsehead | 0.858 | 0.886 | **0.901** | 0.863 | 0.886 |
| Skull and Crossbones * | 0.826 | 0.839 | 0.875 | 0.858 | **0.882** |
| ASI585 wide field | 0.851 (2) | 0.846 | **0.869** | 0.866 | 0.866 |

- **PD failed: armF does not reproduce armD** (0.739 on Rim against the 0.80 bar), so **there is no
  size recipe and that question is closed.** Three disjoint 8-session sets give 0.825 / 0.726 /
  0.739 on Rim.
- **But nearly the whole armD-vs-armF gap is ONE observer.** Excluding Rim and averaging the other
  two clean cells: armD 0.885, armF 0.876, 21-any 0.866, armE 0.865, 60-any 0.855. On Horsehead,
  Skull and ASI585 armD and armF are the same model to within seed spread, and armF is ahead on
  Skull. **armD is uniquely good on Rim Nebula and nowhere else is much separated** -- which
  retro-shades this whole line's origin, since v19's table was measured on Rim and only Rim.
- **PF failed: the operator property does not replicate a third time.** armF's kept_shared is
  **0.701**, in the many-session band (armD 0.626, armE 0.631, 60-any 0.710, 21-any 0.716). So
  1l's headline -- few-session models preferentially strip pair-shared residue -- was two draws
  agreeing. What DOES survive across all five configurations is `kept_total`: 0.241 / 0.263 /
  0.271 for the three 8-sets against 0.331 / 0.336 for the two many-sets, no overlap. Few-session
  models strip harder overall; they do not preferentially keep shared residue.
- **The decisive number is a spread, not a mean.** armF's three seeds give kept_shared 0.640 /
  0.762 / 0.701, a range of 0.122 -- larger than the entire few-vs-many gap of 0.084 it was
  predicted to sit inside. One training set's seeds span both "bands". **Draw-to-draw and
  seed-to-seed variance is larger than every effect this programme has chased since v17.**
- **What replicates**: fine structure (1.5-4 px) at matched noise, where armD and armF agree
  closely (Rim 0.974 both, then 1.017/1.012, 1.016/1.009, 0.996/1.002) and both clear the
  many-session arms (0.90-0.99). Nebulosity stays 0.973-1.001 for every arm, so P10 holds again
  and no arm trades it.
- \* armD trains on a session from the same folder AND night as the Skull observer (two mosaic
  panels of one night named by different anchor objects) -- a home advantage v20's observer
  rotation could not see, since it rotated observers with armD fixed. **armF beats armD in that
  cell anyway**, so the contamination is real in principle and bought armD nothing measurable.

**N4 is unchanged and now correctly framed: ship v19d as the best checkpoint MEASURED, not as the
output of a method.** If it is ever retrained, re-measure rather than assume. v17c is worth naming
as the lower-variance alternative -- it uses 60 of 63 available sessions so it has almost no draw
variance left, and it means 0.830 against armD's 0.861 over the clean observers.

### 1o. Shipping it (N4): the dial we planned was the wrong one, and the model had a colour cast

2026-08-17. `n2n_v19d_s2` is exported, wired and pinned. Working notes and the numbers behind each
claim: `D:\Astro-Dataset\n2n-smoke\ship\README.md`. Three findings, in the order they surfaced.

**The seed matters and was never picked.** Over the three uncontaminated observers, faint amplitude
at matched noise 0.90 is s0 0.869, s1 0.844, **s2 0.883**. Picked on that table rather than on the
gate, which is single-observer. The spread is 0.039, so the same "best MEASURED, re-measure a
retrain" caveat that applies to the checkpoint applies to the seed inside it.

**The conditioning dial is not shippable, and the plan had named it as THE dial.** `with_sigma`'s
`strength` was to be exposed directly: free, no retraining. Measured over all four observers against
the blend at matched noise, it loses on three independent grounds, any one sufficient.

1. **It cannot reach gentle.** At `strength` 0.15 -- a 6.7x understatement of sigma -- three of four
   observers still sit below the noise the blend reaches at a = 0.1. Scaling the conditioning down
   does not scale the residual correction to zero, so the dial saturates long before "barely touch
   it", which is most of the range a user wants.
2. **Its reachable span varies by 4x between observers** (0.072 on Horsehead, 0.273 on Skull), so one
   knob position means different things on different data.
3. **Fabricated point sources RISE toward its gentle end, by 2.6x to 6.3x** (Rim 0.71 -> 4.50 per
   tile above the input's own bar). Told its input is clean, the model reads noise as signal and
   sharpens it. A control labelled "less" that invents more is not shippable at any documentation
   budget.

Where both are measurable it is behind anyway (-0.066 and -0.017 on Rim, +0.008 on Skull). So the
shipped dial is the blend `input + a * (denoised - input)`: exactly monotone, spans the full range
to untouched by construction, and being a convex combination of two images that already exist it
cannot invent. The graph keeps a `strength` input pinned at 1.0.

**The model has a per-channel level prior, and it would have shipped as a colour cast.** Over 49
held-out tiles the shift in a channel's median correlates with that channel's input level at
**-0.988** (and only -0.278 with its noise): the net drags an input toward the sky level of its
eight training sessions. Because the prior is per channel it lands unequally -- the worst held-out
tile moved R +0.017, G +0.002, **B +0.048**. Any master whose sky sits below the training set's
would have come out blue. `N2nLinearRunner.RestoreLevel` adds back the per-channel constant that
restores the source median, per chunk, where the shift is produced. It is free with respect to
everything the checkpoint was selected on: a per-channel constant moves the per-channel std by at
most 3.7e-9 and the background sigma by 1.7e-7, so the frontier numbers stand unchanged.

**Verification.** The exported graph reproduces torch to max |diff| 1.49e-7 (5 ppm of the tile
noise). The whole C# path -- NCHW packing, median-fill border, 256 px chunking, edge-chunk
replicate pad, level restore, rim-dropping stitch, blend -- reproduces torch to 5.07e-7 on the worst
sampled pixel, pinned by `N2nDenoiserTests.TheWholePipelineReproducesTorch` against a fixture both
languages generate from the same stated LCG. That test was seen to FAIL (by 80x its tolerance) with
the level restore removed, so its green means something.

**Not the default `IDenoiseEnhancer`.** `AddTianWenN2nDenoiser` is opt-in. The model is measured
against its own ablations on held-out astro masters and has never been compared against
`OnnxDenoiser` on the enhance pipeline's own job; it is also OSC-only and throws on mono, where the
AI4 family has a weight bundle. Making it the default would assert a comparison nobody ran, silently,
on every `--ai-backend sas` run.

**Distribution (decided 2026-08-17): the weights live IN the repo, at
`src/TianWen.AI.Imaging/models/`.** At 3.1 MiB the model is test-fixture-sized, so it ships like one.
Three consumers hang off that one location. The test project copies it beside the binaries, so
`N2nDenoiserTests` resolve the checkout's own weights ahead of the per-user cache, and the
cross-language parity test therefore runs on every push against exactly the bytes being shipped
instead of skipping. End users get it through `tools/tianwen-ai-models-fetch.ps1` phase 4, which
hardlinks from the checkout into `%LOCALAPPDATA%/TianWen/models` (the SAS Pro trick).

**It is a plain git blob, NOT an LFS object, and that is a deliberate exception with a revert note.**
The repo-wide `*.onnx` LFS rule still stands; `.gitattributes` exempts this one directory from it.
The reason is measured rather than stylistic: the LFS budget was exhausted, and reproducing the
workflow's cache key across main showed this was the **only** file missing from the runners' cached
object set (32 cached against 33 wanted), so storing it as a blob was the whole fix and every other
LFS object still rides the cache. The cost is 3.1 MB of permanent history against a 976 MB `.git`
(0.3%), and it is permanent: removing the file later does NOT reclaim it without a history rewrite.
Two consequences to know. A checkout carries the real weights whether or not git-lfs is installed.
And the `.onnx` in CI's narrow-pull glob is now a leftover no-op, kept only because the glob also
feeds the LFS cache key. `ModelResolver` still refuses a pointer stub and keeps probing, so the
logged-skip failure mode survives the revert. **Revert when the replacement model lands (expected
2026-09):** drop the `.gitattributes` exemption, then `git rm --cached` + `git add` the file. The
standing cost of the LFS shape it reverts to: every retrain that ships adds ~3 MiB to LFS storage,
fine at this size and wrong for anything AI4-sized.

**Reachability (decided 2026-08-17): `--ai-backend n2n`, plus an Auto rescue tier.** Until now the
model was DI-opt-in only -- usable from code, invisible to `image sharpen` / `stack --enhance` / the
server endpoint / the viewer. `EnhanceBackend.N2n` closes that, with three deliberate semantics:

- **`n2n` means "the in-house model where this role has one, Auto everywhere else."** One options
  record threads through every pipeline step, so the star remover and deconvolver see the value too
  and must keep working; scoping it per role is what makes the flag composable rather than a trap.
  Routed by the same `DeferredEnhancer.Resolve` that arbitrates RC-vs-SAS, so there is still exactly
  one selection path.
- **Auto gains a rescue tier, not a preference.** Auto's denoise chain is RC (present + licensed)
  -&gt; SAS -&gt; N2N, where the N2N tier fires ONLY when the SAS AI4 weights are not on disk and the
  input is OSC at the default variant (`DeferredDenoiser.Pick`, logged as a warning). With the SAS
  weights installed, Auto is byte-for-byte the old path -- the rescue converts "fresh checkout,
  no 300 MB AI4 fetch -&gt; FileNotFoundException" into a working enhance with the 3 MiB in-repo
  model. It replaces a crash, never a measured backend's result, which is what keeps the
  never-compared-against-AI4 rule intact. A mono input or Lite/Walking variant falls through to
  SAS unchanged, whose missing-model error names the bundle that could actually serve it.
- **The strength dial rides the existing knob, which lost its product name.** The flags shipped as
  `--bxt-sharpen` / `--nxt-denoise` / `--nxt-iterations` when they only steered RC-Astro; the moment
  a second backend read one, the product prefix became a lie, so they are now `--deblur-sharpen` /
  `--denoise-strength` / `--denoise-iterations` (hard rename, no aliases -- the wire DTO fields were
  generic all along, so only the CLI surface moved). `EnhanceTuning.DenoiseStrength`
  (`--denoise-strength`) is "how much denoising" in [0, 1] whatever the backend: RC maps it to
  `nxt --dn`, N2N maps it to the blend (`out = in + s*(den - in)`, null = 1.0). No second flag for
  the same user intent.

The viewer's right-click backend cycle gains the fourth stop (Auto -&gt; RC -&gt; SAS -&gt; N2N,
label `Enhance (N2N)`); the server endpoint inherits the value through the shared
`EnhanceOptions.TryParse`. Pinned by `EnhanceOptionsTests` (parse), `RcAstroPhase3Tests`
(the routing matrix: explicit n2n, degrade-to-Auto without a lane, and the four-row rescue
matrix over a temp-dir `ModelResolver`), and `N2nDenoiserTests.TheTuningDenoiseStrengthDrivesTheBlend`
(the options path is the identical computation to the direct strength path, checked span-equal).

> **Correction, 2026-09-02.** The wiring notes above and `ship/README.md` say this net "trained on
> LINEAR [0,1] tiles taken straight from stacked masters". It did not: `DatasetTileExporter` stores
> every tile after `ApplyInputStretch`, and the eval cache measures per-channel medians of 0.249 to
> 0.250 with a sub sigma of 0.0082 (the `SIGMA_SCALE` comment's "near 0.01"). `N2nLinearRunner`
> therefore fed the graph a domain about 100x below the one it trained on. Every number in 1o that
> was measured on TILES stands. **The real-frame path was re-measured the same day under
> [denoiser-training.md](denoiser-training.md) H0 (E0.5) and the fix shipped:** on the 163-sub Bubble
> master, in one process, the verbatim path removed 10 / 9 / 17 percent of the noise (R/G/B) and
> cut every star's peak by about 30 percent at every SNR (amplitude kept 0.70, flat from SNR 8 to
> 100+), with a per-chunk level drag of 0.074 on a sky of 0.0019; through the exporter's stretch the
> same weights remove 13 / 23 / 36 percent, keep 0.73 of a faint star's amplitude rising to 0.93 at
> the bright end, move no background pixel by more than 10 MAD, and the drag falls to 0.0029 on a
> level of 0.25. `N2nLinearRunner` now applies `ApplyInputStretch` to the whole frame, runs, and
> inverts with `MtfUnstretch`; the runner, the enhancer, the ship README and this section are
> corrected. The 30 percent flat star suppression was not predicted and is the largest single
> defect the skew caused; the full table is in the denoiser plan's run log.

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
| **N12** | **The next campaign is planned in [denoiser-training.md](denoiser-training.md)** (2026-09-02): H0 the runner domain fix (above), H1 synthetic noise injection for master depth, H2 noise shape, H3 the level prior, H4 broadband transfer (the SV545 `IDAS LPS-D3` nights), H5 capacity, H6 observer-pool resolution. N3, N10 and N11 below are absorbed there as H4, the "no size recipe" invariant and H6. | plan | Everything below this row is pre-registered there with arms and kill criteria. |
| **N3** | **Restate the deployment target.** The pool is 3 nm + quad-band + zero broadband. Either accept OSC narrowband as the target (and say so everywhere the docs claim broadband), or deliberately acquire broadband training data. | doc | Section 0. Everything measured so far is a narrowband result wearing a general label. |
| ~~**N4**~~ | ~~**Ship a checkpoint behind a strength dial.**~~ **DONE 2026-08-17, see 1o.** `n2n_v19d_s2` exported to ONNX with the conditioning baked in, wired as an opt-in `IDenoiseEnhancer` (`AddTianWenN2nDenoiser`), pinned by a cross-language parity test. Two things changed on the way: the shipped dial is the BLEND, not `with_sigma`'s `strength` (measured and rejected), and the model needed a per-channel level restore it did not have. Distribution (2026-08-17): in-repo at `src/TianWen.AI.Imaging/models/` as a plain git blob (an `.gitattributes` exemption from the `*.onnx` LFS rule, taken because the LFS budget was exhausted and this was the one object missing from the runners' cache; revert expected 2026-09), materialized by the fetch script, parity-tested in CI -- see the end of 1o. | medium | Done. |
| ~~**N8**~~ | ~~**Run v23: the causal test + the which-8 arm.**~~ **DONE 2026-08-16, all three predictions failed, see 1m.** Pair-time moves the operator by -10% to -27% (wrong direction), and armE shows the count claim was never established. | 2 h | Closed the residue pathway and, more valuably, found that 1i had a confound nobody had checked. |
| ~~**N9**~~ | ~~**armF: a THIRD disjoint 8-session set.**~~ **DONE 2026-08-16, see 1n.** armF does not reproduce armD (0.739 vs 0.825 on Rim), so there is no size recipe; and armD-vs-armF is a tie everywhere except Rim. PF also failed, retiring 1l's surviving half. | 1 h | Closed the recipe question and showed the draw/seed variance exceeds every effect chased since v17. |
| **N10** | **Ask what armD has on RIM specifically** -- narrowed by 1n from "what is armD like" to a single cell, because armD ties armF on every other observer. Compare armD / armE / armF on per-session PSF width, pixel scale and focal length, sky background and integration depth, from the stores that already hold them (`psf-sessions.jsonl` et al). Descriptive, no training. **The target property must explain faint STARS and not structure**: on Rim armD and armF keep fine structure identically (0.974 both) while differing 0.086 in faint-star amplitude. | small | Optional and bounded. Do NOT widen it back to 65 sessions x N properties -- at that width something always correlates, and 1n has already established the honest headline without it. |
| **N11** | **Decide whether the 4-observer evaluation is strong enough to select a shipping model at all.** 1n's spread (one arm's three seeds spanning both operator "bands"; three 8-sets spanning 0.10 on Rim) says the measurement's resolution is comparable to the differences being ranked. Either widen the observer pool (task #11 bakes more archive) or state the selection's uncertainty beside the checkpoint. | doc, or gated on #11 | Cheap to state, and it is the difference between "v19d is the best model" and "v19d measured best on four sessions". N4 should carry whichever is true. |
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
- Checkpoints: `C:\temp\tianwen-scratch\n2n-big\n2n_v17{b,c}_s{0,1,2}{,_final}.pt` (`_final` = selected).
  **The scratch moved from `C:\tianwen-scratch` to `C:\temp\tianwen-scratch`** (verified 2026-09-02;
  the old root no longer exists). The trainer's `--cache` default and the `EVAL` constants in the
  `ship/` and `v24/scripts/` files still name the old path; pass `--cache` explicitly. Shipped arm:
  `C:\temp\tianwen-scratch\n2n-d8\n2n_v19d_s2_final.pt`.
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
  the run records**: v13/first-8 is `C:\temp\tianwen-scratch\n2n\meta.json`, v15 is `n2n-ds`, v17 is
  `n2n-big`, v19d (shipped) is `n2n-d8`, armE `n2n-e8`, armF `n2n-f8`, the four-observer eval cache
  `n2n-eval4`; each holds `train_sessions` + `val_sessions` by name.
- Filter verdicts: `D:\Astro-Organized\_provenance\group-{a,b,c}-locked.csv`, with per-session basis.
