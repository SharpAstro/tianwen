# Sky-trend rule measured on every captured night (2026-11, first half)

Issue #2037 measures #2030's sky-trend rule (`LightGrading.SkyOverTrend` and `SkyRising`) against the owner's own
`BAD_` / `BAD_CLOUD_` file-name marks. This is the first half: the measurement and the two tables the first and third
questions need. Questions 2, 4 and "does the brightened sky fall back" are headed placeholders at the end, for the next leg.

## Method

- **Tool.** `tools/sky-trend-probe`, built with `-p:SkyTrend=true` over #2030's two Lib files (`LightGrading.cs`,
  `CloudRejection.cs`). Those two files were overlaid from the PR head, whose subject is "docs(grade): the sky trend's reach is
  stated per cadence and the span path's 5 minute reach is pinned", and restored afterwards; this branch carries no change under
  `src/`.
- **Grading.** Each folder is graded two ways. The viewer path reads each file with `Image.TryReadFitsFile`, scales by peak, takes
  stars at SNR 10 (at most 2000) and calls `LightGrading.Grade`. The live-judge path uses the frame as read, 1000 stars, one
  `LiveCloudJudge` per rig, replayed with the 15 minute `CloudWaitTimeout`.
- **No clear-night references.** `SkyRising` only counts where `ReferenceNight` is default, so the run's own baselines decide.
  A night with a reference would be graded differently (#1976).
- **Marks** are the owner's file names (`BAD_`, `BAD_CLOUD_`, `MAYBE_CLOUD_`), read as truth. A mark is not always cloud: see
  the twilight note below.
- **Inputs were read only.** Per-frame CSVs (`frames.csv`, `stretches.csv`, `rise-stretches.csv`, `live.csv`, `scores.csv`,
  `summary.md`) stay in scratch, not in the repository.
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
| 2026-05-14 | 30 s | 898 | 89 | 37 | 0 | 123 | 2 |
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
  3 on 07-06 are of that kind, so the 75 of them are never catchable by a cloud rule. Cloud marks proper: 123 of 198.
- **Every D: folder has no `BAD_` mark.** Precision and recall there are undefined; only firings can be judged. SkyRising did not fire on any of
  the 11 (595 frames), and the unmarked "FP (verdict)" of Helix RGB (6 frames) comes from the patchiness term, not the sky trend.
- The large FP (verdict) counts on 05-14 (123), 07-06 (100) and 05-11 (25) are other terms (night sky, patchiness, live judge) on
  nights with few or no marks. They are not this measurement's subject; the owner's marks there are sparse, and they are listed so the
  size of the unmarked pool is visible.

## Question 1: marked-cloud stretches

A stretch is a run of marked frames in one series. "Reach" is the longest a held rise can read as cloud at that cadence, from the
doc comment on `SkyOverTrend` (51 min at 1 min subs, about 40 at 3 to 5 min, 30 at 10, 15 at 15, none at 20; at 30 s subs the
count path alone is about 55 min). "Read clear again" counts frames inside the stretch whose verdict is not cloud.

| night | target | length (min) | frames | cadence | caught (patchy or other / run / night / SkyRising) | read clear again | bank longer than reach? | Sun alt | kind |
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
  one read clear, at its start. No frame inside either read clear again later, so the "a haze that stays is caught for its first
  hour only" worry did not bite: the night-sky, run and patchiness terms carried the rest.
- **The only stretch with a mid-bank gap is 09-12 LMC**: five frames at 04:00 to 04:05 (sky 1.03 to 1.04 over its trend, stars
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
  rising stretches fell back within minutes; the last ran to the end of the series. This needs the owner's eye on the frames; it
  is **not** counted as a true positive here.
- **2026-05-14 Rim, 2 frames (30 s subs), 23:41.** Sky 4 % above its line for two frames (1.041 and 1.046), stars unchanged (4779
  against 4800), patchiness dipping. A blip that fell straight back: a false call at the 4 % bar at 30 s cadence.

### Precision and recall

| subset | SkyRising TP | FP | FN | precision | recall | note |
|---|---|---|---|---|---|---|
| 1 min nights with cloud marks (09-10, 09-12) | 4 | 0 | 82 | 1.00 | 0.05 | 86 marked on those two nights |
| 30 s nights, strict against the marks | 0 | 29 | 92 | 0.00 | 0.00 | 05-14, 07-06; the 29 are the two groups above; 92 marked, 55 of them twilight |
| every folder with a cloud mark (123 cloud marks) | 4 | 29 | 119 | 0.12 | 0.03 | against the marks only |
| subs of 2 minutes or longer (11 folders, 595 frames) | 0 | 0 | n/a | n/a | n/a | no marks; no firing |

Precision against the marks is 0.12, but 27 of the 29 are the 07-06 Lagoon stretch that may be real cloud; if it is, precision
is 0.94 (31 of 33) and the strict number is a ceiling on the damage. Recall is low because the existing terms already
catch most cloud and SkyRising only adds frames at the start of a rise.

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
finding: one blip is not a measurement.

## Question 2

*Placeholder, for the next leg.*

## Question 4

*Placeholder, for the next leg.*

## Does the brightened sky fall back?

*Placeholder, for the next leg.* The data is in hand in `rise-stretches.csv` per folder (FELL-BACK against KEPT-RISING-TO-END):
2026-05-14 one stretch (fell back), 2026-07-06 six (five fell back, one kept rising to the end of its series), 2026-09-12 one (fell
back, the four marked opening frames of LMC). The next leg reads these against the Moon and the star-flux series before it concludes
anything.
