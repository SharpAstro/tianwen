# continue.md

Handoff rewritten 2026-08-14, branch `feat/detection-purity-probe`. Supersedes the 2026-08-11
version entirely (that one described PR #148, since merged, on a branch that no longer exists).

**This file is now TRACKED and pushed**, unlike the previous version. It was deliberately untracked
so it would show in `git status`, but that also meant it only ever existed on one box, which is the
opposite of what a handoff is for. It costs one line of `git status` noise; delete it when the work
lands.

## TL;DR

Nothing is running. No jobs in flight, no background processes, no open PRs. The dataset is baked
and verified; the last three sessions of work were the P1 denoiser training experiments, which are
complete and recorded. The branch had **never been pushed** (43 commits, no upstream) and now is.

## What landed since the last handoff

The bake, the star-detection work and the PSF measurement are all done and covered by commits on
this branch; read `git log` for those. The part that is NOT obvious from the log:

### The N2N denoiser experiments (2026-08-13/14), and two retractions

Full record with checkpoints, logs and scripts: **`D:\Astro-Dataset\n2n-smoke\v10\README.md`**.
Design + conclusions are in `docs/plans/ai-denoise-deconv.md` (§3a and the "Optimisation" bullet).

**Half-master pairs are a NEGATIVE result.** Wired in as a fourth sampling regime and trained three
seeds per arm, they leave the noise/amplitude frontier where it was and invent *more* point sources
at matched noise (0.75x noise: control +5.1 in a −2.4..10.2 range, half pairs +21.4 in 17.2..25.6).
The depth argument that motivated them still measures out (a half is 0.215x a sub against a 4-sub
average's 0.591x, through the same rejecting integrator); it just does not cash out in training.

**The stopping rule beats every config change tried.** The loss falls monotonically while invented
sources climb from +1 per tile over the raw-sub floor at step 250 to +38 at step 4000, so every
extra step improved the objective and degraded the product. Selecting on a mid-training fabrication
probe instead: **+22% faint-star amplitude and 46% fewer invented sources for 1.6% more noise**,
replicated 3/3 seeds, two of three strictly dominating at equal or better noise.

**Two things I reported and then had to retract, both worth not repeating:**

1. `n2n_smoke.py` never seeded torch. numpy WAS seeded, which is the worst case: two runs drew the
   same tiles in the same order with identical per-regime step counts, so they *looked* controlled
   while starting from different weights. A third run of the same config landed on the far side of
   the control. Every A/B before 2026-08-14 in that series measured the initialisation. Fixed with
   `--seed` covering init and tile draw plus `cudnn.deterministic`; two runs of one seed are now
   bit-identical at no throughput cost.
2. "The gate found an excellent checkpoint" was one probe squeaking under a threshold nothing else
   could reach. `|resid corr| <= 0.20` rejected 117 of 120 probes, was the sole reason 39 times, and
   sat below the 5th percentile of 0.229; the one pass read 0.199. Relaxing it to 0.30 was also
   wrong: measured across two held-out sessions the metric moves 0.301 for one checkpoint while the
   spread across six different checkpoints on one session is 0.160, so it cannot gate at any
   setting. It is now report-only, replaced by a minimum-denoising floor.

The gate's current shape: `spurious over floor <= 6`, `faint amp >= 0.60`, `noise <= 0.82`, then
minimise noise among passers. Residual correlation reported, not gated.

## Repo state

- Branch **`feat/detection-purity-probe`**, 22 commits ahead of `origin/main`, now pushed with an
  upstream set. It had no upstream at all before today.
- `origin/feat/per-channel-psf` was a **stale remote-tracking ref** for a branch deleted after PR
  #148 merged; `git fetch --prune` cleared it. The remote only ever had `main` plus this branch.
- No open PRs. Opening one for this branch is the obvious next step and was not done because the
  work is mid-stream; `gh pr create --base main` when ready.
- Working tree clean apart from this file.

## Outstanding tasks

The in-session task list does NOT survive a restart, so this is the durable copy. Numbers match the
session list at the time of writing.

### Dataset / stacking

| # | Item | Notes |
|---|---|---|
| 20 | Carry `BadPixelDetection` into the dataset path | **In progress.** Drizzled hot pixels sit in 45 of the training masters. Links to #32 below: shared unrejected residue is the leading hypothesis for why half-master pairs fabricate. |
| 21 | Collapse the dataset registrar onto the stacking core | They parallel each other and have drifted both ways before. |
| 22 | Derive the bad-pixel map from registration, not from the dark | |
| 23 | Pre-rebake checklist | Fix everything below before burning another ~4.5 h bake. |
| 19 | Per-channel flux banding inverts channel 0's field-radius profile | The red channel's profile runs backwards; it is banding, not optics, and it survives the drizzle re-bake. |
| 25 | Temporal + tolerance semantics for calibration matching | **Parked by user direction** ("not sure we should faff around with some misbehaving scaled darks"). |
| 11 | Bake older years of `D:\Astro-Pics` | Detached via `Start-Process`; expect new TELESCOP spellings. |
| 10 | Shoot a QHY294 g1600 dark library | Hardware, needs a night. Un-drops 3 Newtonian sessions. |
| 15 | Harvest gradient fields from the retained masters for P5 | |

### P1 denoiser

| # | Item | Notes |
|---|---|---|
| 31 | Retrain on a genuinely SHORTER schedule | The gate selects step 1100-1700 of 4000 every time, so half the budget makes the model worse. Truncating a long run is not the same as training a short one; try a schedule whose LR actually decays inside the useful window. Three seeds, paired against the v13 selections already scored. |
| 32 | Test the shared-residue mechanism for half-pair fabrication | N2N preserves what the two views SHARE. Check whether the sources the half-pair model invents sit where BOTH halves carry a coincident local maximum. Note `corr(A − master, B − master)` is algebraically forced to −0.99 and cannot be used. |
| 30 | Score the denoised half against B, not the master | The master CONTAINS A, so amplitude-kept starts at 0.99 and can only be spent. B's noise is independent, inflating every model's error by the same constant and leaving the ranking clean. |
| - | Extend the metric suite (user's item 3, not started) | The transfer test suggests the first addition should be a per-SESSION breakdown rather than another metric: two sessions already disagree by more than most models do. Also split every per-channel and 1-2 px figure on `MasterStrategy` (47 of 67 masters are BayerDrizzle; averaging them with AHD sessions throws away what the re-bake bought). |

## Paths and commands

```
Dataset root    D:\Astro-Dataset\2025-2026-darkscaled     (the current bake; calgated is the old one)
  store         stats\psf-sessions.jsonl                  (THE source of truth for FWHM)
  manifest      tiles-manifest.jsonl                      (no FWHM column, by design)
N2N records     D:\Astro-Dataset\n2n-smoke\
  v8            the chosen config before this series
  v9            RETRACTED (unseeded torch); measurements stand, comparisons do not
  v10           the seeded verdict: v10-v13, 15 runs, README has the whole arc
Training cache  C:\tianwen-scratch\n2n-ds                 (1200 cells, 11 slots incl. half pairs)
Scratch         C:\tianwen-scratch                        (NVMe; D: is USB HDD at 37 MB/s)

Re-render the dataset report (no archive scan, works with D: unmounted):
  tianwen dataset report --out D:\Astro-Dataset\2025-2026-darkscaled
```

The N2N scripts live in `D:\Astro-Dataset\n2n-smoke\v10\scripts`. The reusable half is
`n2n_frontier.py` (compare configs at matched noise, not matched step count), `n2n_gateaudit.py`
(which gate is binding, and what relaxing it surfaces), `n2n_gatetransfer.py` (does a metric survive
a change of session) and `n2n_compare_figure.py` (labelled comparison, panels captioned in-image).

Tile format is raw fp16 little-endian, 256 x 256 x 3, so exactly 393,216 bytes per tile.

## Constraints still in force

- A multi-hour build must run detached via `Start-Process`, never the Bash background tool.
- `--resume` does NOT skip the ~19k-header archive scan, which is seek-bound on the USB spindle.
  Avoid needless restarts.
- Never write destructively to `C:\temp\astro` or the `D:\Astro-Pics` originals. Scratch copy first.
- **Never read a file a running job appends to.** A `Get-Content` of `psf-sessions.jsonl` failed a
  session 65 of 68 into a bake. Read the console log, or copy first.
- RC-Astro EULA section 10 forbids using RC-Astro or its outputs to create, train, test or benchmark
  a competing model.
- Do not create local nupkg feeds or run `dotnet pack` to short-circuit a sibling release. Extend the
  `UseLocalSiblings` auto-detect instead.
- A push to `main` triggers `publish-nuget`. PR branches are safe.
- Do not change behaviour without evidence.
- `pwsh` never `powershell`; `python` never `python3`; CRLF for `.cs` and `.csproj`; no em-dashes.
- Use the SSD for anything I/O-heavy so a job is not starved by D:.

## Method notes worth keeping (they cost real time to learn)

- **Compare configs at matched noise, never at matched step count.** A config that trains more
  gently reads as "keeps more amplitude, removes less noise" at a fixed budget while sitting on the
  same frontier, merely less far along it.
- **Several seeds per arm, always.** Here one arm's fabrication count spanned 15.1 to 34.4 across
  three seeds against the other's 32.6 to 33.6.
- **Audit a threshold against the metric's distribution before believing a pass rate.** A bar below
  the achievable range vetoes rather than selects, and reads as a rare find.
- **A ratio objective can be maximised by doing nothing.** `faint_amp / noise` is exactly 1.0 for the
  identity, which also passes every purity gate trivially.
- **A metric whose session-to-session shift exceeds its across-model spread cannot gate at any
  setting.** Measure that before adopting one, not after.
