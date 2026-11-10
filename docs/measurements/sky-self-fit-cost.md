# What the expected-sky self-fit costs the render thread (2026-11)

Issue #1974 (light grade: an expected sky per site plus an absolute transparency) lets the viewer fit an expected-sky model from a
night's own frames when the site has no stored model for the rig (`ExpectedSkyModel.Fit`, run lazily per night and object). A
review note on PR #2004 (the PR for #1974) asked what that costs the render thread for a folder of 300 to 2000 frames, which had
never been measured. This page is that measurement, and what it does and does not say.

## Method

- **Tool.** `SkySelfFitBenchmarks` in `src/TianWen.UI.Benchmarks`, BenchmarkDotNet 0.15.8 with `MemoryDiagnoser` and
  `[Params(300, 1000, 2000)] Frames`. It was run in process (`--filter *SkySelfFitBenchmarks* --inProcess`), because the default
  out-of-process toolchain rebuilds the whole sibling project graph for every benchmark and timed out before measuring anything.
  The class also carries a short-run job, whose rows read `NA` in the raw report because its generated build reached
  BenchmarkDotNet's 2 minute build timeout; the numbers below are the in-process job's, which used BenchmarkDotNet's own
  iteration counts. 12 benchmarks executed (4 cases at 3 sizes).
- **Machine.** Intel Core i7-10700 2.90 GHz (8 physical, 16 logical cores), Windows 11 25H2, .NET SDK 10.0.403, runtime 10.0.14
  x64 RyuJIT, Release build.
- **Not a quiet box.** It ran as a pilot job at below-normal priority on a busy shared node, and without `--alone`. Read the sizes
  and the ratios, not the last digit. The standard deviation is 2 to 7 % of the mean on the fit rows (2.06 of 35.2 ms at 300
  frames, 9.04 of 124.5 ms at 1000, 6.56 of 284.9 ms at 2000), and the 99.9 % confidence half width of every mean is under 3 %.
- **The night.** Synthetic, one rig and one object, 30 s exposures at 1 minute spacing, crossing the meridian, the last eighth of the
  frames under a thin veil (+7 % level, +86 % gradient), at site latitude -37.9 and longitude 145.2. It has the shape of
  `LightGradingTests.SyntheticSkyNight`, which the grade's own tests read the veil from. The stored model for the "stored" case
  is fitted to a clear night of the same size.
- **What a verdict is.** The file list calls `LightGrading.Verdict(grades, grade, references, expectedSky)` for every painted row
  (`ImageRendererBase.FileList.cs`) and so does the info strip (`InfoPanelData.cs`). A verdict's baselines are built once per
  grade-table SNAPSHOT (a `ConditionalWeakTable` keyed on the immutable dictionary), and the self-fit is a `Lazy` per night and
  object inside them. `ViewerState.RecordFrameGrade` swaps in a new snapshot for every grade it records, so the first row painted
  after a grade lands pays for the baselines and, with no stored model, for the fit. The session's `LiveCloudJudge` and the
  stacker pass `selfFit: false` and never pay it.
- **The four cases.** (1) `ExpectedSkyModel.Fit` over the night, alone. (2) The first verdict after a new grade with no stored model
  (baselines and self-fit). (3) The first verdict after a new grade with a stored model (baselines only). (4) A verdict on a
  snapshot already built, which is what every other painted row costs.
- **Raw output.** The BenchmarkDotNet CSV, HTML and markdown reports stay in scratch, not in the repository.

## Results

Mean time per operation:

| frames | (1) fit alone | (2) first verdict, no stored model | (3) first verdict, stored model | (4) verdict on a built snapshot |
|---:|---:|---:|---:|---:|
| 300 | 35.2 ms | 38.6 ms | 2.37 ms | 0.71 us |
| 1000 | 124.5 ms | 142.0 ms | 9.07 ms | 0.73 us |
| 2000 | 284.9 ms | 297.9 ms | 19.5 ms | 0.73 us |

Allocated per operation (managed, BenchmarkDotNet's 1 KB = 1024 B):

| frames | (1) fit alone | (2) first verdict, no stored model | (3) first verdict, stored model | (4) verdict on a built snapshot |
|---:|---:|---:|---:|---:|
| 300 | 4.1 MiB | 4.7 MiB | 519 KiB | 80 B |
| 1000 | 13.2 MiB | 14.9 MiB | 1.7 MiB | 80 B |
| 2000 | 26.0 MiB | 29.6 MiB | 3.6 MiB | 80 B |

- **The fit is about 120 to 140 us per frame and grows a little faster than linearly**: 117 us per frame at 300, 124 at 1000 and 142
  at 2000 (8.1 times the cost for 6.7 times the frames).
- **The fit is nearly all of the first verdict when there is no stored model.** The difference between cases (2) and (3) is 36, 133
  and 278 ms, against 35, 125 and 285 ms for the fit alone in case (1), so the two agree to within 7 %. The baselines alone cost
  7.9 us per frame at 300 and 9.8 us at 2000.
- **A verdict on a built snapshot is free**: 0.7 us, 80 B, whatever the folder size. A screenful of 40 rows is about 30 us.

## Reading for the render thread

A 60 Hz frame is 16.7 ms.

- **With a stored model the first paint after a new grade costs 0.14 of a frame at 300 frames, 0.54 at 1000 and 1.17 at 2000.** The
  baselines alone are within budget up to roughly 1700 frames and over it beyond.
- **With no stored model it is a stall of about 2 frames at 300 (39 ms), 9 at 1000 (142 ms) and 18 at 2000 (298 ms).** This is a
  hitch a user can see from a few hundred frames up, paid by the thread that draws.
- **It recurs, because the snapshot does.** Every recorded grade is a new snapshot, so the first row painted after each landing
  pays again, for as long as grades keep landing, and the fit's size is the number of frames graded so far (the table is the end
  state; the cost at i frames is the cost at i). How many snapshots a folder open produces depends on how star detection
  delivers its grades, and this measurement does not count them.
- **The fit allocates 4 to 26 MiB each time**, about three generation-0 collections per first verdict at 2000 frames, so it also
  adds collector work to the process that draws.
- **A folder of several objects pays one fit per object and night**, each over its own frames and each the first time a row of that
  object is painted. The table is one object's night, the largest single fit.
- **Unaffected:** the session's judge, the stacker, and any folder whose rig has a stored model (case 3).

## What this does not say

- **No quiet-box numbers.** A rerun with `--alone` on an idle node would tighten the digits; it cannot move a result of tens to
  hundreds of milliseconds against a 16.7 ms frame.
- **A synthetic night, not a real one.** It has Gaussian scatter and one veil. Whether a real night's scatter and cloud change the
  robust fit's cost is not measured; the frame count is the only thing varied here.
- **Not a decision (when this was measured; #2069 later took the background fit, see "What changed").** The repo's own pattern for work like this is a `Task` the consumer polls (CLAUDE.md, "Concurrency"), and the
  grade already has a value for "no model yet": no term, which reads NaN and never a guess. Fitting each object's night on a
  background task and reading NaN until it lands would take the stall off the render thread, at the price that the veil term
  appears a moment after the folder opens. Whether to do that, or to refit only after the night has grown by some share, is the
  owner's call and is not part of this review leg.

## What changed (#2069)

The "Not a decision" item above was taken up. The fit no longer runs on the thread that draws:

- **The fit runs on the pool.** `LightGrading` keeps a memo of self-fits keyed on the object-night. A verdict that asks for a self-fit
  (`selfFit: true`) starts the fit there and reads NaN, no term, until it has landed; the stacker and `LiveCloudJudge` pass
  `selfFit: false` and never start one. The viewer's frame loop compares `LightGrading.SelfFitGeneration` and repaints the file list
  and the info strip when a fit lands, so the veil term appears a moment after the folder opens.
- **A fit is reused across snapshots.** Each snapshot's baselines carry the object-night's time-ordered samples and a fingerprint
  (count plus a content hash). A snapshot with the same fingerprint reuses the fit; a changed one starts a new fit only when none is
  in flight for that object-night, and until it lands the previous model is read, so a folder load, whose snapshots change at every
  grade, cannot stack fits. The memo keeps at most 256 object-nights.
- **The numbers above are the fit's own cost**, now paid on a pool thread. They are no longer render-thread stalls: cases (1) and (2)
  of the table are the time a fit takes to land, and the first verdict on a new snapshot costs the baselines alone (case 3).
