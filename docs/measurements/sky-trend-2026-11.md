# Sky-trend rule measured on every captured night (2026-11)

Issue #2037 (this measurement) measures the sky-trend rule of PR #2030 (the grade's sky-trend change) (`LightGrading.SkyOverTrend` and `SkyRising`) against the owner's own
`BAD_` / `BAD_CLOUD_` file-name marks. Questions 1 and 3 come first, then question 2 (the live judge), question 4 (the Moon),
the fall-back table with the 2026-09-12 bank, and a recommendation for each of #2030's design questions at the end.

## Method

- **Tool.** `tools/sky-trend-probe`, built with `-p:SkyTrend=true` over #2030's two Lib files (`LightGrading.cs`,
  `CloudRejection.cs`). Those two files were overlaid from the PR head, whose subject is "docs(grade): the sky trend's reach is
  stated per cadence and the span path's 5 minute reach is pinned", and restored afterwards; this branch carries no change under
  `src/`.
- **Grading.** Each folder is graded two ways. The viewer path reads each file with `Image.TryReadFitsFile`, scales by peak, takes
  stars at SNR 10 (at most 2000) and calls `LightGrading.Grade`. The live-judge path uses the frame as read, 1000 stars, one
  `LiveCloudJudge` per rig, replayed with the 15 minute `CloudWaitTimeout`.
- **No clear-night references.** `SkyRising` only counts where `ReferenceNight` is default, so the run's own baselines decide.
  A night with a reference would be graded differently (#1976, the stored clear-sky references).
- **Marks** are the owner's file names (`BAD_`, `BAD_CLOUD_`, `MAYBE_CLOUD_`), read as truth. A mark is not always cloud: see
  the twilight note below.
- **Inputs were read only.** Per-frame CSVs (`frames.csv`, `stretches.csv`, `rise-stretches.csv`, `live.csv`, `scores.csv`,
  `summary.md`) stay in scratch, not in the repository, in `D:/temp/tianwen-scratch/tia2037/`: `<night>/` (the E: nights) and
  `sc-<folder>/` (the D: SharpCap folders) hold the first run, `v2-<night>/` the live re-run that adds `live_over_trend` and
  `live_sky_rising` to `frames.csv` (and `first_maybe_cloud`, `first_give_up`, `give_up_on_marked` to `live.csv`), and
  `syn-2026-10-17/` the `--synthetic-moon` run. Every night was shot from one site (Melbourne, lat -37.9, lon 145.2) in AEST,
  UTC+10, so a local time is the UTC time plus 10 hours (daylight saving began on 2026-10-04, after every live night).
- **The live replay is an upper bound on distinct give-ups.** `ReplayLive` restarts a spell after each timeout, as the session
  does (`_cloudySince[i] = null`), but it does not model the recovery pause the session takes after one, so a run of cloud that
  the session would have handled once can show as several give-ups here (09-10 has four in one series).
- **Columns.** "Existing terms" is `in_cloud_run`, `term_patchy_or_other` or `term_night`; "SkyRising" is `sky_rising`; a
  "false call" is a frame the verdict (any term) calls cloud that carries no mark. The "caught by SkyRising" column counts
  only marked frames no existing term caught.
- **Count path or span path** is not recorded by the probe. It was derived per firing: a firing is count path when at least ten
  earlier frames of its series lay inside the preceding hour and were neither in a cloud run nor risen themselves, else span path.
  This approximates the grader's own bookkeeping (it also drops frames already cloud for any reason).

### Spot-check before trusting the rest

Both reference nights reproduce #2030's table exactly, with the probe untouched:

| night | #2030 | this run |
|---|---|---|
| 2026-09-12 | 37 of 42 caught, 10 unmarked flagged | 37 of 42 caught (33 by existing terms, 4 by SkyRising only), 10 unmarked flagged |
| 2026-09-10 | 42 of 44 caught, 1 unmarked flagged | 42 of 44 caught (all by existing terms), 1 unmarked flagged |

### What could not be graded

- **2026-04-21** has only SNAPSHOT frames and **2026-07-05** only DARK and FLAT: no LIGHT folder, nothing to grade.
- **Leo Triplet** is CR2, which the FITS-only probe does not read.
- **D:/Astro-Unsorted** holds processed stacks, not subs, so it carries no sky series.
- The D: long-sub folders read are the 11 SharpCap folders below. Nothing there carries a `BAD_` mark.

## Per-night table

Sub length, frames, marked, and what caught the marked frames. `FP (verdict)` is every unmarked frame the verdict calls cloud
by any term; `FP (SkyRising)` is the unmarked frames SkyRising alone fired on.

| night | sub | frames | marked | caught, existing terms | caught, SkyRising only | FP (verdict) | FP (SkyRising) |
|---|---|---|---|---|---|---|---|
| 2025-03-07 | 60 s | 53 | 0 | 0 | 0 | 7 | 0 |
| 2026-05-11 | 30 s | 603 | 0 | 0 | 0 | 25 | 0 |
| 2026-05-14 | 30 s | 898 | 89 | 36 | 0 | 123 | 2 |
| 2026-07-06 | 30 s and 60 s | 700 | 3 | 0 | 0 | 100 | 27 |
| 2026-07-17 | 30 s | 770 | 0 | 0 | 0 | 15 | 0 |
| 2026-08-31 | 60 s | 430 | 20 | 0 | 0 | 0 | 0 |
| 2026-09-10 | 60 s | 220 | 44 | 42 | 0 | 1 | 0 |
| 2026-09-12 | 60 s | 350 | 42 | 33 | 4 | 10 | 0 |
| 2026-10-17 | 60 s | 250 | 0 | 0 | 0 | 14 | 0 |
| SharpCap Eri Grp | 240 s | 24 | 0 | n/a | n/a | 0 | 0 |
| SharpCap NGC 55 RGB | 240 s | 28 | 0 | n/a | n/a | 0 | 0 |
| SharpCap Fornax Group | 240 s | 36 | 0 | n/a | n/a | 0 | 0 |
| SharpCap Orion | 240 s | 17 | 0 | n/a | n/a | 0 | 0 |
| SharpCap Helix RGB | 120 s | 46 | 0 | n/a | n/a | 6 | 0 |
| SharpCap Eta Car (135 mm) | 120 s | 83 | 0 | n/a | n/a | 0 | 0 |
| SharpCap Horsehead (135 mm) | 120 s | 65 | 0 | n/a | n/a | 0 | 0 |
| SharpCap Rosette | 120 s | 21 | 0 | n/a | n/a | 0 | 0 |
| SharpCap M42 | 60 s | 116 | 0 | n/a | n/a | 0 | 0 |
| SharpCap M42 f/2.0 | 64 s | 18 | 0 | n/a | n/a | 0 | 0 |
| SharpCap NGC 253 | 32 s | 141 | 0 | n/a | n/a | 0 | 0 |

Reading it:

- **SkyRising is the only term that ever named a frame no other term caught, and it did so on one night**: the first four frames
  (03:55 to 03:59) of the LMC bank on 2026-09-12, all marked. Nothing else on 20 folders.
- **A mark is not always cloud.** On 2026-05-14 (Eagle and Omega), 2026-08-31 and 2026-07-06 (Lobster, three frames) the marked
  frames sit at Sun altitudes of about -18 to -1.5, -18 to -13 and -18 degrees: dawn or dusk, which the grade treats as the Sun's
  doing and no cloud term judges. 52 of 89 marks on 05-14 (stars gone, 20 left by the floor, Sun to -1.5), all 20 on 08-31 and the
  3 on 07-06 are of that kind, so the 75 of them are never catchable by a cloud rule. Cloud marks proper: 122 of 198 (counted by the verdict: on 05-14 one
  twilight frame also set the night-sky flag without a cloud verdict, so 36 and not 37 of its marks are caught).
- **Every D: folder has no `BAD_` mark.** Precision and recall there are undefined; only firings can be judged. SkyRising did not fire on any of
  the 11 (595 frames), and the unmarked "FP (verdict)" of Helix RGB (6 frames) comes from the patchiness term, not the sky trend.
- The large FP (verdict) counts on 05-14 (123), 07-06 (100) and 05-11 (25) are other terms (night sky, patchiness, live judge) on
  nights with few or no marks. They are not this measurement's subject; the owner's marks there are sparse, and they are listed so the
  size of the unmarked pool is visible.

## Question 1: marked-cloud stretches

A stretch is a run of marked frames in one series. "Reach" is the longest a held rise can read as cloud at that cadence, from the
doc comment on `SkyOverTrend` (51 min at 1 min subs, about 40 at 3 to 5 min, 30 at 10, 15 at 15, none at 20; at 30 s subs the
count path alone is about 55 min). "Uncaught" is frames minus caught: every frame of the stretch whose verdict is not cloud,
including frames before the first caught one. The probe's own `read_clear_again` counts only frames that read clear AFTER the first
caught frame; it is 5 for 09-12 LMC and 0 for every other cloud stretch below (52 on the 05-14 Eagle twilight stretch, which is not
cloud).

| night | target | length (min) | frames | cadence | caught (patchy or other / run / night / SkyRising) | uncaught | bank longer than reach? | Sun alt | kind |
|---|---|---|---|---|---|---|---|---|---|
| 2026-09-12 | LMC | 41.9 | 35 | 1 min | 30 (9 / 17 / 0 / 4) | 5 | no (41.9 < 51) | -29.6 to -21.7 | cloud |
| 2026-09-12 | Helix | 2.0 | 3 | 1 min | 3 (3 / 0 / 0 / 0) | 0 | no | -35.0 | cloud |
| 2026-09-12 | Helix | 3.7 | 4 | 1 min | 4 (4 / 0 / 0 / 0) | 0 | no | -37.5 to -38.2 | cloud |
| 2026-09-10 | Helix | 53.5 | 29 | 1 min | 27 (19 / 4 / 4 / 0) | 2 (the first frames) | yes (53.5 > 51) | -56.9 to -54.3 | cloud |
| 2026-09-10 | Helix | 78.7 | 15 | 1 min | 15 (8 / 6 / 1 / 0) | 0 | yes (78.7 > 51) | -54.0 to -44.0 | cloud |
| 2026-05-14 | Virgo | 38.7 | 36 | 30 s | 35 (35 / 0 / 0 / 0) | 1 | no | -47.3 to -54.6 | cloud |
| 2026-05-14 | Eagle and Omega | 87.8 | 53 | 30 s | 1 | 52 | yes, but twilight | -18.1 to -1.5 | twilight |
| 2026-08-31 | NGC 1962 | 23.7 | 20 | 1 min | 0 | 20 | no, twilight | -18.1 to -13.4 | twilight |
| 2026-07-06 | Lobster | 2.0 | 3 | 1 min | 0 | 3 | no, twilight | -17.6 to -18.0 | twilight |

What the table says:

- **The two stretches longer than the reach (09-10) were caught by the other terms, not by SkyRising.** Two frames of the first
  went uncaught, at its start (the probe's `read_clear_again` is 0). No frame inside either read clear again later, so the "a haze that stays is caught for its first
  hour only" worry did not bite: the night-sky, run and patchiness terms carried the rest.
- **The only stretch with a mid-bank gap is 09-12 LMC**: five frames at 04:00 to 04:05 (sky 1.02 to 1.04 over its trend, ratios 1.039, 1.035, 1.027, 1.021 and 1.024, stars
  intact, patchiness 19 to 23) read clear, flanked by cloud on both sides. SkyRising did not catch them because 1.04 is its bar
  and the sky sat just below it; they were not bridged by the 15 minute bracket either. The cloud run then took over from 04:11.
- **SkyRising's whole contribution to the marks is the four opening frames of 09-12 LMC** (ratios 1.049 to 1.070, all above the
  4 % bar and, per #2030, above 6 sigma), before a run could form. That is the value the rule was written for.
- **No stretch was longer than the reach at its cadence while uncaught and cloud**: the uncaught ones are twilight.

**Recommendation (question 1).** Keep the reach as stated. On the marked nights it never decided a catch that the other terms
missed, and the longest cloud banks (53 and 79 minutes) ran past it without a gap. This measurement cannot say what happens to a
cloud bank at 3 minute or longer subs, because none of the folders at those cadences carries a mark (see question 3). The 5 frame gap in
09-12 LMC is the one miss to look at in the next leg (does a 3 % bar bridge it, and at what false-call cost).

## Question 3: count path against span path, and the bars at 3 minutes and longer

### Firings, by path

SkyRising fired 33 times on 20 folders; every firing was **count path**. The **span path never fired**: its only role is where
fewer than ten earlier frames lie in the hour, which needs subs of about 6 minutes or longer, and no folder at that cadence has
a run long enough, or a mark.

| path | firings | marked | unmarked | where |
|---|---|---|---|---|
| count | 33 | 4 | 29 | 09-12 LMC 4 (marked); 07-06 Lagoon 27 (unmarked); 05-14 Rim 2 (unmarked) |
| span | 0 | 0 | 0 | none |

### The unmarked firings, looked at against their own series

- **2026-07-06 Lagoon, 27 frames (30 s subs), 01:31 to 01:59 local.** Sky 1.05 to 1.32 over its trend while the scored star count
  fell from about 4800 to between 2000 and 3900 and the star flux fell by 7 to 18 % against the hour before. Moon at 23 to 28
  degrees, 60 % illuminated, Sun near -66. Sky up and stars down together looks like thin cloud or haze the owner did not mark
  (only 3 frames of this night are marked, all twilight), not the Moon, which brightens the sky without taking stars. 5 of the 6
  rising stretches dipped under the bar on the next frame; the last ran to the end of the series. The star flux fall (0.82 to
  0.95 against the hour before) is the evidence that it is **probably cloud**; the LMC series that follows is read as cloud by the
  night-sky term for about 50 minutes, but that is a different target at a different altitude and Moon separation, which can trip
  that term alone, so it is only suggestive. These would be true positives the owner never marked, **if the owner confirms** it. The
  table below counts them against the marks, strictly, and then restates it with them as cloud.
- **2026-05-14 Rim, 2 frames (30 s subs), 23:41.** Sky 4 % above its line for two frames (1.041 and 1.046), stars unchanged (4779
  against 4800), patchiness dipping. A blip that fell straight back: a false call at the 4 % bar at 30 s cadence.

### Precision and recall

| subset | SkyRising TP | FP | FN | precision | recall | note |
|---|---|---|---|---|---|---|
| 1 min nights with cloud marks (09-10, 09-12) | 4 | 0 | 82 | 1.00 | 0.05 | 86 marked on those two nights |
| 30 s nights, strict against the marks | 0 | 29 | 92 | 0.00 | 0.00 | 05-14, 07-06; the 29 are the two groups above; 92 marked, 55 of them twilight |
| every folder with a cloud mark (122 cloud marks) | 4 | 29 | 118 | 0.12 | 0.03 | against the marks only |
| subs of 2 minutes or longer (8 folders, 320 frames) | 0 | 0 | n/a | n/a | n/a | no marks; no firing |
| every D: folder (11 folders, 595 frames) | 0 | 0 | n/a | n/a | n/a | three of them are 60, 64 and 32 s (M42, M42 f/2.0, NGC 253) |

Precision against the marks is 0.12, but 27 of the 29 are the 07-06 Lagoon stretch, which the star flux fall suggests is cloud
onset (fall-back section), **if the owner confirms** it. Counted so, about 31 of 33 firings would be correct (the two misses are
the 05-14 Rim blip, two frames), and the strict number is a floor. Recall is low because the existing terms already catch most cloud and SkyRising only adds frames at the
start of a rise.

### Do the 4 % and 6-sigma bars hold at 3 minutes and longer?

No cloud mark exists at any cadence of 2 minutes or more, so **recall there is unmeasured**. What can be said is the false-call
margin on clear skies, from `SkyOverTrend` over every read frame:

| cadence (median) | folders | frames read | largest ratio on a clear sky | frames at or over 1.04 |
|---|---|---|---|---|
| 4 min | Eri, Fornax, NGC 55, Orion | 77 of 105 | 1.029 | 0 |
| 2 min | Etacar, Helix, Horsehead, Rosette | 161 of 215 | 1.028 | 0 |
| 1 min (SharpCap) | M42, M42 f/2.0 | 114 of 134 | 1.022 | 0 |
| 1 min (E:) | 03-07, 08-31, 09-10, 09-12, 10-17 | 1170 of 1303 | see below | see below |

At 1 minute the E: nights give a clear-sky margin as well: the largest ratios on nights with no cloud marks are 1.027 (03-07) and
1.012 (10-17). 08-31 reaches 1.043 on six frames (twilight marks, not cloud) and SkyRising did not fire on any.

- **The 4 % bar has at least 1 point of margin over the largest clear-sky ratio at 2 to 4 minutes** (1.029 at most), and the
  6-sigma half never decided: no clear frame got near it.
- **The cost of a short window is read frames, not false calls**: at 4 minutes only 41 to 83 % of frames could be read (Orion 7 of
  17, Eri 18 of 24), the rest sit before the trend has enough earlier frames. A short run at long subs is simply not judged.
- **Nothing here tests the span path**: it needs a marked cloud bank at about 6 to 15 minute subs, which the archive does not hold in
  these folders.

**Recommendation (question 3).** Keep 4 % and 6 sigma as they are. They hold at 1 minute on every measured night and cost no false
call from 2 to 4 minutes, but that is a statement about false calls, not about whether the rule catches cloud there. Do not
describe the span path as measured. Two follow-ups for the owner: (a) look at the 07-06 Lagoon frames, since the answer moves the
30 s precision from 0.00 to near 0.9; (b) mark a cloud bank on a 3 to 10 minute sub night when one occurs, which is the only thing that can
test the span path. A two-frame minimum before a 30 s firing counts (as the 05-14 Rim blip suggests) is a candidate, not a
finding: one blip is not a measurement. Follow-up (a) is open: the star flux fall says the Lagoon frames are probably cloud, which
would put 30 s precision near 0.94, but only if the owner confirms them; until then it is 0.00 against the marks.

## Question 2: SkyRising in the live judge

The live-judge path was re-run on the six nights that had live spells, with the probe recording whether the judge's own
`SkyRising` fired on each frame (`live_over_trend`, `live_sky_rising`). A **spell** is a run of `MAYBE_CLOUD_` frames in one
series. It is **caused by SkyRising** when every cloud frame in it was flagged by SkyRising and by no existing term (patchiness,
night sky, run), else by the existing terms. A spell **passes the wait** when it lasts the 15 minute `CloudWaitTimeout`, which
is the point where the session takes the condition-deterioration path. "Marked" counts frames carrying the owner's marks; an
unmarked frame is not proved clear, only unmarked.

| night | spells | caused by SkyRising | caused by existing terms | spells touching a mark | passed the 15 min wait (marked / unmarked) |
|---|---|---|---|---|---|
| 2026-09-12 | 9 | 1 (4 frames, all marked) | 8 (all marked) | 9 | 0 |
| 2026-09-10 | 5 | 0 | 5 (all marked) | 5 | 4 (4 / 0) |
| 2026-07-06 | 11 | 7 (25 frames, none marked; the live judge's own count, the viewer path's is 27) | 4 (night-sky term, 73 frames, none marked) | 0 | 3 (0 / 3) |
| 2026-05-14 | 8 | 1 (2 frames, unmarked) | 7 | 3 | 4 (1 / 3) |
| 2026-07-17 | 4 | 0 | 4 (patchiness, 13 frames, unmarked) | 0 | 0 |
| 2026-05-11 | 1 | 0 | 1 (night-sky term, 8 frames, unmarked) | 0 | 0 |
| **total** | **38** | **9** | **29** | **17** | **11 (5 / 6)** |

### When the live judge would have fired, per night and series

Times are UTC from `v2-<night>/live.csv` (`first_maybe_cloud`, `first_give_up`, `give_up_on_marked`); local is UTC+10 and falls on
the next calendar day for the evening hours. "First give-up" is the 15 minute `CloudWaitTimeout` reached, where the session takes
the condition-deterioration path. "Marked" says whether the spell's frames carry the owner's marks.

| night | series | first `MAYBE_CLOUD_` (UTC / local) | first give-up (UTC / local) | give-up on |
|---|---|---|---|---|
| 2026-05-11 | C2025 R3 | 09:12:22 / 19:12 | none | n/a (one 5 minute spell, 8 frames, unmarked) |
| 2026-05-14 | Virgo | 09:22:49 / 19:22 | 10:30:17 / 20:30 | unmarked frames (the spell began 10:15:05) |
| 2026-05-14 | Rim | 13:41:06 / 23:41 | none | n/a (the SkyRising blip, 2 frames) |
| 2026-05-14 | Eagle and Omega | 19:08:35 / 05:08 | 19:23:54 / 05:23 | unmarked frames |
| 2026-07-06 | Lagoon | 15:31:45 / 01:31 | none | n/a (seven SkyRising spells, longest 4.3 minutes) |
| 2026-07-06 | LMC | 16:11:30 / 02:11 | 16:26:48 / 02:26 | unmarked frames |
| 2026-07-17 | Helix | 17:23:21 / 03:23 | none | n/a (four short spells, patchiness) |
| 2026-09-10 | Helix | 14:24:13 / 00:24 | 14:48:12 / 00:48 | marked frames (10 of 10) |
| 2026-09-12 | Helix | 11:04:53 / 21:04 | none | n/a (two short spells, 6 of 7 frames marked) |
| 2026-09-12 | LMC | 17:55:31 / 03:55 | none | n/a (seven spells, 4 frames or fewer, every frame marked) |

All eleven give-ups, with what ended each and whether it lies on the owner's marks:

| night | series | spell start (UTC) | give-up (UTC / local) | frames | marked | what raised it | on |
|---|---|---|---|---|---|---|---|
| 2026-05-14 | Virgo | 10:15:05 | 10:30:17 / 20:30 | 24 | 0 | existing terms | clear sky, a target lost for nothing (Sun -50 to -35, Moon down) |
| 2026-05-14 | Virgo | 11:36:16 | 11:52:10 / 21:52 | 25 | 25 | existing terms | marked cloud, right |
| 2026-05-14 | Eagle and Omega | 19:08:35 | 19:23:54 / 05:23 | 24 | 0 | existing terms | unmarked, dawn approaching (Sun -24 to -21, Moon rising at 0 to 3 degrees) |
| 2026-05-14 | Eagle and Omega | 19:24:24 | 19:39:51 / 05:39 | 24 | 0 | existing terms | unmarked, Sun -21 to -18, then the owner's twilight marks begin at 19:40 |
| 2026-07-06 | LMC | 16:11:30 | 16:26:48 / 02:26 | 28 | 0 | existing terms (night sky) | unmarked, Moon at 30 degrees, 60 % lit |
| 2026-07-06 | LMC | 16:27:19 | 16:47:08 / 02:47 | 12 | 0 | existing terms (night sky) | unmarked |
| 2026-07-06 | LMC | 16:47:39 | 17:02:39 / 03:02 | 31 | 0 | existing terms (night sky) | unmarked |
| 2026-09-10 | Helix | 14:24:13 | 14:48:12 / 00:48 | 10 | 10 | existing terms | marked cloud, right |
| 2026-09-10 | Helix | 14:49:13 | 15:05:51 / 01:05 | 9 | 9 | existing terms | marked cloud, right |
| 2026-09-10 | Helix | 15:22:31 | 15:49:13 / 01:49 | 6 | 6 | existing terms | marked cloud, right |
| 2026-09-10 | Helix | 16:03:38 | 16:36:47 / 02:36 | 5 | 5 | existing terms | marked cloud, right |

Counts, separated by cause:

| spells caused by | give-ups | on marked frames (right) | on unmarked frames |
|---|---|---|---|
| SkyRising | 0 | 0 | 0 |
| the existing terms | 11 | 5 (Virgo 11:36, Helix 09-10 four times) | 6 (Virgo 10:15, Eagle twice, LMC 07-06 three times) |

The six unmarked give-ups are not proved clear: the 07-06 LMC pair of spells is the night-sky term at a target with the Moon at 30
degrees, and the two Eagle spells sit at dawn with a 5 % Moon rising, where the owner's marks start only at Sun -18. Even
read as false timeouts they are the existing terms', none of them SkyRising's. The SkyRising spells that did not give up, with
their first moments: 09-12 LMC 17:55:31 UTC (marked, 11 minutes ahead of the existing terms' 18:06:49), 07-06 Lagoon from
15:31:45 (seven spells, 25 live frames; unmarked, probably cloud), 05-14 Rim 13:41:06 (two frames, unmarked, a blip).

- **Spells SkyRising caused: 9 of 38, and none lasted past 7 minutes.** All nine are shorter than the 15 minute wait, so
  SkyRising **caused no give-up on any of the six nights**. Every one of the 11 give-ups is an existing term's: night sky (07-06
  LMC, three spells), patchiness (05-14 Eagle twice, Virgo 10:15), and cloud banks the owner marked (09-10 four times, 05-14
  Virgo 11:36).
- **On marked frames: 1 of the 9.** The opening four frames of the 09-12 LMC bank, every one marked. The existing terms' own spell
  began at 18:06:49 UTC, so SkyRising named the bank 11 minutes earlier (4 frames).
- **On unmarked frames: 8 of the 9.** Seven are the 07-06 Lagoon stretches (25 live frames, 15:31 to 15:59 UTC), which the
  fall-back section reads as probably cloud onset on the strength of the star flux. One is the 05-14 Rim blip (2 frames), a false call.
- **Of the 29 existing-term spells, 11 passed the wait, 5 on marked cloud and 6 on unmarked frames** (07-06 LMC three times,
  05-14 Eagle twice, Virgo 10:15). Those six are not SkyRising's and not this question's, but they are the larger
  false-timeout pool: six unmarked 15 minute give-ups against none from SkyRising.

**Recommendation (question 2): keep SkyRising in the live judge, ungated.** On six nights it started 9 spells, none long enough
to give up, one of them 11 minutes ahead of the owner's marked bank and seven on a stretch with the star flux down (probably
cloud, pending the owner). Its one false call was a two-frame blip. Nothing here shows it trips the 15 minute give-up. The caveat belongs to question 4: a rising Moon is the
case that could make it hold a spell open, and the real archive has none.

## Question 4: the Moon

Real nights first. Moon altitude and illumination over each run (every series that night), the frames with the Moon up, the
largest `SkyOverTrend` ratio over the frames with the Moon between -5 and 20 degrees (the rise window), the sky's fitted slope
over the clear frames of a series (the bar is 4 %, which over a 51 minute reach is about 0.08 %/min), and what SkyRising called
those frames.

| night | Moon altitude over the run | illumination | frames Moon up | largest ratio, rise window | clear-sky slope, %/min | SkyRising on those frames |
|---|---|---|---|---|---|---|
| 2026-09-10 | -59 to -38 | 0.00 to 0.01 | 0 | none (never up) | -0.17 | 0 |
| 2026-09-12 | -46 to -19 | 0.02 to 0.03 | 0 | none (never up) | -0.06 to -0.10 | 0 (4 on cloud, below) |
| 2026-05-14 | -64 to 20 | 0.05 to 0.08 | 106 | 1.023 | -0.10 to -0.17 | 0 (2 on Rim, Moon down) |
| 2026-07-17 | -60 to 13 | 0.12 to 0.15 | 104 | 1.023 | -0.17 to 0.00 | 0 |
| 2026-05-11 | -45 to 12 | 0.29 to 0.33 | 85 | 1.023 | -0.09 to +0.06 | 0 |
| 2025-03-07 | -6 to 8 | 0.59 to 0.60 | 29 | 1.027 | -0.32 | 0 |
| 2026-10-17 | -17 to 37 | 0.38 to 0.40 | 156 | 1.008 | -0.11 | 0 |
| 2026-08-31 | -9 to 39 | 0.87 to 0.90 | 388 | 1.043 | 0.00 to -0.09 | 0 (the six frames at 1.043 are twilight marks) |
| 2026-07-06 | -47 to 38 | 0.60 to 0.64 | 310 | 1.017 | -0.17 to +0.10 | 27 on the viewer path, 25 live, on Lagoon with the Moon at 23 to 28 degrees |

- **Seven real moonrises (03-07, 05-11, 05-14, 07-17, 08-31, 10-17, 07-06) and no SkyRising firing in the rise window.** The
  largest ratio there is 1.043 (08-31, a twilight artefact SkyRising did not call), at the 4 % bar and under its sigma half.
  A real Moon at 12 to 90 % illumination, in this archive, cost nothing.
- **The one night where SkyRising fired with the Moon up is 07-06 Lagoon** (60 % lit, 23 to 28 degrees). The Moon cannot explain
  it: star flux fell 4 to 18 % against the hour before at the same time, and the Moon takes no stars. A rising Moon of that
  size should leave star flux unchanged.
- **The trend absorbs a slow rise.** The sky's slope on the nights with a rising Moon is within about 0.1 %/min of zero, because
  the hour before the Moon appears sets a trend the Moon only slowly leaves. That is why the real data is quiet, and why it is
  not proof of safety.

**Synthetic Moon (labelled synthetic, not a measurement of any real night).** The probe's `--synthetic-moon` adds a Krisciunas
and Schaefer (1991) Moon to the clear night **2026-10-17** (250 frames, 60 s subs, no marks, 14 unmarked verdict calls from other
terms) as four scenarios: a 25 % crescent and a 50 % quarter, each rising mid-run and before dawn. The star frames are
untouched, so star flux stays flat.

| scenario | Moon altitude | SkyRising frames | largest ratio | star flux against the hour before | live-judge MAYBE_CLOUD_ frames | live spells past the 15 min wait |
|---|---|---|---|---|---|---|
| crescent, rises mid-run | -5 to 33 | 40 (14:15 to 15:03 UTC) | 1.23 | 1.06 | 40 | 2 |
| crescent, rises before dawn | -5 to 16 | 35 (15:36 to 16:23 UTC) | 1.22 | 1.00 | 35 | 2 |
| quarter, rises mid-run | -5 to 33 | 40 | 1.77 | 1.06 | 59 | 3 |
| quarter, rises before dawn | -5 to 16 | 35 | 1.74 | 1.00 | 47 | 2 |

- **A synthetic Moon fools SkyRising in all four scenarios**, 35 to 40 frames each, and every stretch is KEPT-RISING-TO-END (none
  falls back). The live judge then keeps spells open past 15 minutes two or three times, which on a real run is the
  condition-deterioration path. The 25 % crescent alone does it, and its rise is about 0.4 %/min, five times the bar's own rate.
- **The synthetic model is broadband.** It scales the whole sky by a Krisciunas and Schaefer factor on a 100 nL dark sky, with no
  allowance for the narrowband filters these rigs use, and lays it on a night that already had a real 38 % Moon. It is an upper
  bound, and the filter is not modelled. The star frames are untouched, so "star flux separates it from cloud" holds by
  construction here and is not evidence about a real Moon.
- **The one real waning crescent before dawn in the archive is 05-14 Eagle and Omega**: a 5 % Moon rising at 0 to 6 degrees with the
  Sun at -24 to -18 (19:08 to 19:40 UTC). SkyRising did not fire there; the existing patchiness term gave up twice on unmarked
  frames (question 2), and the largest ratio of the night's rise window is 1.023. Dawn and Moon cannot be told apart on that
  stretch, which is itself the finding: the one real case is entangled with twilight.
- **The synthetic rise is much larger than any real one.** The real 10-17 night had a 38 % Moon rising from -17 to 37 degrees
  and the sky stayed at 1.008 of its trend; the synthetic 25 % crescent at the same hours reaches 1.23. So the model is an upper
  bound on what a Moon does to this camera and sky. It shows that the rule has no defence against a Moon bright enough to
  matter, not that one has done so.
- **Star flux is the discriminator, and its margin is thin.** The synthetic Moon leaves flux at 1.00 to 1.06; the real cloud
  firings sit at 0.82 to 0.98 (09-12 LMC 0.975, Lagoon 0.82 to 0.95, the Rim blip 0.94). A gate of "flux below 0.98" would block
  all four synthetic stretches and pass every real firing, but 09-12 LMC (0.975) and the 0.995 synthetic stretch are only 0.02
  apart, and four marked frames are too few to set it.

**Recommendation (question 4): add no Moon term now. Base the trend on the expected sky once #1974 (the expected-sky model) lands**, with the Moon and
the target's altitude in it, so a rise the geometry predicts is not a deviation. A standalone Moon term would be tuned on
synthetic data and a margin of 0.02. Until #1974, nothing in the real archive needs it (seven moonrises, no firing). If a Moon
false call is seen on a real night first, the star-flux gate above is the interim candidate.

## Does the brightened sky fall back?

Every SkyRising stretch (a run of `SkyRising` frames in one series, from the probe's `rise-stretches.csv`) is FELL-BACK when the
sky returned to its trend before the series ended, KEPT-RISING-TO-END when it did not. "Flux" is the stretch's star flux
against the hour before.

| night | target | start (UTC) | minutes | frames | Sun altitude | outcome | peak over trend | flux against hour before | marked | what it was |
|---|---|---|---|---|---|---|---|---|---|---|
| 2026-09-12 | LMC | 17:55 | 3.7 | 4 | -29.6 to -28.9 | FELL-BACK | 1.068 | 0.975 | 4 | cloud |
| 2026-07-06 | Lagoon | 15:31 | 2.7 | 5 | -69.0 to -68.6 | FELL-BACK | 1.111 | 0.927 | 0 | probably cloud |
| 2026-07-06 | Lagoon | 15:38 | 0.5 | 2 | -68.0 to -67.9 | FELL-BACK | 1.087 | 0.895 | 0 | probably cloud |
| 2026-07-06 | Lagoon | 15:39 | 0.0 | 1 | -67.8 | FELL-BACK | 1.066 | 0.953 | 0 | probably cloud |
| 2026-07-06 | Lagoon | 15:40 | 0.0 | 1 | -67.6 | FELL-BACK | 1.057 | 0.922 | 0 | probably cloud |
| 2026-07-06 | Lagoon | 15:48 | 4.3 | 8 | -66.4 to -65.7 | FELL-BACK | 1.328 | 0.823 | 0 | probably cloud |
| 2026-07-06 | Lagoon | 15:53 | 6.1 | 10 | -65.5 to -64.4 | KEPT-RISING-TO-END | 1.248 | 0.860 | 0 | probably cloud |
| 2026-05-14 | Rim | 13:41 | 0.5 | 2 | -69.4 | FELL-BACK | 1.045 | 0.943 | 0 | blip, a false call |

- **All eight stretches are at Sun altitudes below -28, so twilight cannot be the cause of any.** Zodiacal light, which reaches
  the sky only as the Sun nears -18 or so, is ruled out at the same altitudes.
- **In every stretch the star flux fell (0.82 to 0.98).** A brighter sky with fewer stars is cloud: haze scatters light into the
  sky and takes light from the stars. The Moon, twilight and zodiacal light would give the first without the second.
- **Seven of eight dipped under the 1.04 bar on the very next frame (0.5 to 1.0 minute later), and the eighth ran to the end of
  its series**, the last Lagoon stretch. FELL-BACK here is that weak, a single later frame under the bar, at 30 s to 1 minute
  cadence: it does not say the sky stayed down. That includes the 09-12 LMC opening, whose next frame is 1.039 and which sits
  inside a 42 minute marked bank. Read it as "the stretch was not one unbroken rise", not as proof that each was a passing cloud.
  After the Lagoon run the next series (LMC) is read as cloud by the night-sky term for about 50 minutes (the 07-06 LMC spells),
  which is suggestive of an overcast the Lagoon stretches announced, but a different target at a different altitude and
  Moon separation can trip that term alone, so the star flux fall is the better evidence.
- **The Rim stretch is the lone false call** (flux 0.94 on two frames, 4.5 % over): a blip. Two frames is also the shortest
  stretch that fired, so a "two frames at 30 s" guard (question 3) would stop it.

### The 2026-09-12 bank, 03:55 to 04:37 local (17:55 to 18:37 UTC)

The LMC series, 200 frames. Local time is UTC+10. Each row is five frames; sky, star count and median star flux are ratios to
the series' median before the bank (frames before 17:55 UTC), and patchiness is the grader's score.

| UTC | first frame | marked of 5 | sky | stars | flux | patchiness | Sun altitude |
|---|---|---|---|---|---|---|---|
| 17:49 | 0160 | 0 | 0.921 | 1.02 | 0.996 | 19.3 | -30.8 |
| 17:55 | 0165 | 5 | 0.959 | 1.00 | 0.963 | 19.4 | -29.6 |
| 18:01 | 0170 | 5 | 0.939 | 1.06 | 0.962 | 22.5 | -28.5 |
| 18:07 | 0175 | 5 | 0.955 | 1.09 | 0.937 | 27.5 | -27.3 |
| 18:14 | 0180 | 5 | 0.921 | 1.05 | 1.020 | 19.8 | -26.1 |
| 18:20 | 0185 | 5 | 0.958 | 1.01 | 0.948 | 22.6 | -25.0 |
| 18:26 | 0190 | 5 | 0.967 | 0.98 | 0.937 | 23.9 | -23.7 |
| 18:33 | 0195 | 5 | 0.984 | 0.93 | 0.926 | 25.0 | -22.5 |

The sky had been falling for hours, to 0.92 of the pre-bank median by 17:49; across the bank it stops falling and ends 0.06
higher, while star flux sinks 4 to 7 % and patchiness moves from a steady 19 to between 22 and 28 (an unevenness across the
frame that twilight and zodiacal light, being smooth, do not produce). The Sun is at -29.6 at the first frame and -22.5 at the
last, both inside astronomical night, and twilight's brightening starts after -18. The Moon is at -46 to -27 and cannot be
involved.

**What the bank was: cloud, with high confidence.** Five things agree: stars fell while the sky rose; patchiness rose with it;
the Sun was 4 to 12 degrees short of where twilight reaches the sky; the Moon was down; and the owner marked every frame
`BAD_CLOUD_`. What is not certain is the last frames: at 18:33 the Sun is at -22.5, and zodiacal light may add a little smooth
rise there, perhaps a point of the closing ratios (1.01 to 1.03). It does not change the verdict, since flux and patchiness have
not recovered by the end of the run at frame 0199.

## Recommendations, one per #2030 design question

1. **The cliff (the reach of the trend, question 1).** Keep the reach as stated: 51 minutes at 1 minute subs, about 40 at 3 to
   5, 30 at 10, 15 at 15, none at 20. Table "Question 1: marked-cloud stretches": the two banks longer than the reach (53.5 and
   78.7 minutes, 09-10) were carried by the other terms, and no cloud frame inside any stretch read clear later except 5 frames
   in 09-12 LMC. Those 5 frames (1.02 to 1.04, sky just under the bar) are the one miss to look at next.
2. **The live judge (question 2).** Keep SkyRising in it, ungated. Table "Question 2": 9 of 38 spells, none past 7 minutes, no
   give-up caused, one 11 minutes ahead of a marked bank, seven on a stretch with the star flux down (probably cloud, if the owner confirms), one two-frame blip. Per night and
   series the first `MAYBE_CLOUD_` and give-up times are in the dated tables; all 11 give-ups are the existing terms', 5 on marked
   cloud and 6 on unmarked frames, none SkyRising's.
3. **Long subs, 3 minutes and longer (question 3).** Keep 4 % and 6 sigma. The largest clear-sky ratio is 1.029 at 2 to 4
   minutes, none at 1.04. The span path never fired and no mark exists at those cadences, so its recall is unmeasured; do not
   call it measured. At 30 s, precision would be about 0.94 if the owner confirms the 07-06 Lagoon frames as cloud (31 of 33), 0.12 against
   the marks only.
4. **The Moon (question 4).** Add no term. Seven real moonrises produced no firing (largest ratio 1.043, a twilight artefact),
   while a synthetic Moon fools SkyRising in all four scenarios and holds the live judge open past the wait, with star flux
   (1.00 to 1.06 against 0.82 to 0.98) the only discriminator and a margin of 0.02.
5. **Base the trend on the expected sky once #1974 lands (the owner's request).** Yes. It is the one change that handles the
   Moon and the target's altitude together, and the only one that does not need tuning on synthetic frames. All eight
   SkyRising stretches here are cloud or a blip with flux down, so the expected-sky trend should keep them and shed what the
   Moon and altitude explain. When it lands, re-run `tools/sky-trend-probe` over the same nights and the four synthetic
   scenarios: SkyRising should stay at 9 live spells, none on the Moon.
