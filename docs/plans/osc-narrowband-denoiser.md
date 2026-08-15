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
| **N1** | **Re-bake from `D:\Astro-Organized`.** This is the whole fix, and it is a ROOT PATH, not code. The organized archive's 6,438 lights already carry `FILTER = 'Optolong L-Ultimate 3nm'` / `'Optolong L-Quad Enhance'` in their headers, are filed `lights/<camera>/<filter>/<target>/`, and have their calibration keyed `<CAMERA>/<TYPE>/<date>-g<gain>-o<offset>-t<temp>[-e<exp>]` with flats under `flats/<camera>/<filter>/<date>`. `Image.Fits` already READS `FILTER`+`FILTCLAS` (with the blank-guard fix) and already WRITES both, so a master built from tagged lights carries the filter forward with no change at all. | ~4.5 h, no code | Everything below depends on the dataset knowing its filter, and the work to make that true was already done and then not used. |
| **N2** | **PSF conditioning in the trainer.** Feed measured per-plane PSF width as a conditioning input, exactly as noise sigma already is. Re-run the 60-session arm against v15's frontier. | ~1 h GPU | The v8 lesson one axis over: the failure was a single-point training distribution, and the fix was making the varying quantity an INPUT rather than narrowing the data. If it works it explains 1b instead of working around it, and keeps all sessions. Task #36, reframed. |
| **N3** | **Restate the deployment target.** The pool is 3 nm + quad-band + zero broadband. Either accept OSC narrowband as the target (and say so everywhere the docs claim broadband), or deliberately acquire broadband training data. | doc | Section 0. Everything measured so far is a narrowband result wearing a general label. |
| **N4** | **Ship a checkpoint behind a strength dial.** `n2n_v17c_s0_final.pt` at 0.80x is already a defensible operating point (+2.0 relative, -19 absolute). Wire as an `IDenoiseEnhancer` in the SAS tier with strength exposed. | medium | A model exists and is not deployed. Independent of N1-N3. |
| **N5** | **Key `FieldRadiusProfiles` by (train, filter)** once N1 has made the filter available from headers. Task #19, reduced to a grouping-key change. | small | After N1 this is a few lines. Before N1 it needed a side-table that should never have been contemplated. |
| **N6** | **Mono narrowband support.** Deferred until good mono data exists; the archive's 2 ASI1600MM sets are assessed as poor and must NOT be used as a baseline. Task #37. |  | The user intends to shoot true narrowband, where per-filter focus removes 1c's root cause at acquisition. |
| **N7** | Red centre-vs-corner star cutouts, if the optical reading ever needs pixel-level confirmation rather than statistical. | small | Optional. The four falsified hypotheses plus train- and filter-dependence already carry it. |

### Open questions worth stating

- **Is the v17 regression a filter effect after all?** The pool is 39 3 nm against 9 quad-band, and
  those are physically different passbands with different channel content. v15's 8 sessions may have
  been filter-homogeneous where the 60 are not. N1 makes this answerable in one query and it should be
  checked BEFORE N2 spends GPU time on the PSF hypothesis.
- **What are the 19 unlabelled sessions?** Mostly the parked ASI585 group (its ADU scale is unresolved
  per [filter-inference.md](filter-inference.md) section 8) plus the 2 mono sets. If they are a
  meaningful share of training cells they are unmodelled heterogeneity.
- **Does PSF conditioning subsume filter conditioning?** A 3 nm frame's red plane is soft because of
  focus, and the conditioning input is the measured width, so possibly the filter never needs to be an
  input at all, only a grouping key for analysis. N2 answers this.

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
- Filter verdicts: `D:\Astro-Organized\_provenance\group-{a,b,c}-locked.csv`, with per-session basis.
