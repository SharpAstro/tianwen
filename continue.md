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

### Then v14 (same day): band conditioning, and a third retraction

Full record: **`D:\Astro-Dataset\n2n-smoke\v14\README.md`**. Six runs, 3 seeds x 2 arms, with the
v13 runs serving as the control arm (same seeds, same config, already scored).

**The half-pair negative is now UNCONDITIONAL.** It had a live escape: a scalar sigma plane cannot
express noise SHAPE, so adding half pairs labels two genuinely different distributions with one
number. The shape gap is real and large (band1/band0 power measured scene-free: 0.601 / 0.596 /
0.589 for 1-sub / 2-avg / 4-avg against **0.320** for a half-master, so every training regime shares
one shape and deployment has another, and sub-averaging only ever closed the LEVEL half of the gap).
Re-run with three band-sigma planes replacing the scalar, half pairs are still null on every metric
at matched noise, and two of three runs never passed the gate at all.

**Band conditioning itself is WORSE and is rejected.** It converges far more slowly and never reaches
below ~0.80x noise in 4000 steps where scalar reaches 0.60-0.65x; it keeps 0.060 less amplitude at
matched noise on the gate session; and on the held-out report session it is equal-or-worse at every
comparable point (matched pair 17.5 invented against 18.3, group ranges overlapping, no band run
below 0.80x).

3. **`n2n_metrics.py` defaults `--cache` to the OLD calgated bake, and I omitted the flag.** So the
   first v14 scoring was cross-bake, which is what produced an apparent 30% fabrication win for band
   conditioning (14.7 against 21.1) that does not exist on the bake the models trained on. Nothing
   was contaminated: neither of that cache's val sessions is in the trainer's train or val split.
   But a bake difference moves fabrication more than a config change does. **The tell was free and I
   ignored it** -- the raw-sub floor printed 20.1 where v10, v12 and v13 all printed 21.2. Always
   pass `--cache`, and check the floor against the previous run before reading any fabrication
   comparison. Only today's run had the bug; every earlier one passed the flag.
4. **I transfer-tested the gate I retired and not the one that replaced it.** `spurious_over_floor`
   fails the same test `resid_corr` did: worst session-to-session delta **8.094** against a **4.875**
   spread across models on one session, and that test used the correct cache throughout, comparing
   two sessions of the SAME bake. So `<= 6.0` is session-calibrated, not a universal purity bar.
   Subtracting the raw-sub floor was supposed to normalise this and does not, because the shift is
   systematic and one-signed. **When you retire a gate, transfer-test its successor**: the successor
   inherits the job, not the evidence.

`n2n_gatenorm.py` then closed the "find a better formula" branch: six candidates, none clearing
merit 1.0, and the current `mean_diff` the best of them at 0.60. But `log_ratio` is worst on
threshold (0.43) and best on ordering (rho +0.857), and ordering survives any monotone session
shift -- so the fix is a RELATIVE stopping rule on `log_ratio` rather than a better absolute bar.
**Watch merit's population dependence**: adding the nine step-4000 checkpoints lifted every
candidate above 1.0 (mean_diff to 1.47) purely by widening the numerator with known-bad models, and
that flattering number answers an easier question than the gate faces.

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
| 33 | ~~Recalibrate the fabrication gate's threshold~~ | **DONE (v15).** Resolved as a guardrail, not a new metric. Reformulation closed (six candidates, none usable, the current one best). A relative rule on `log_ratio` closed too (no margin both stable and useful; the baseline's sign varies by seed, so the arbitrariness just moved into the first few probes). The decisive fact: the fabrication bar rejects NOTHING on the unprobed session (0 of 19 steps; every rejection is the noise bar), because the shift is one-signed and the probed session is the stricter one. But that is an accident of the val ordering, so `--gate-observe` is now the DEFAULT and prints the second trajectory. See `v15/README.md`. |
| 31 | ~~Retrain on a genuinely SHORTER schedule~~ | **DONE (v16), NEGATIVE.** The mechanism was real (a 4000-step run is at ~65% of peak LR when it reaches step 1600, so the gate keeps an un-annealed checkpoint) and it does not cash out: at matched noise, amplitude and invention both overlap the 4000-step control at every level the 1600- and 2400-step arms reach. The extra steps buy frontier RANGE, which is what a gate needs (lowest noise reached 0.58-0.65x at 4000, 0.76-0.85x at 1600, and one short run never cleared the 0.82x bar at all). Keep the long run plus the gate. See `v16/README.md`. |
| 32 | Test the shared-residue mechanism for half-pair fabrication | N2N preserves what the two views SHARE. Check whether the sources the half-pair model invents sit where BOTH halves carry a coincident local maximum. Note `corr(A − master, B − master)` is algebraically forced to −0.99 and cannot be used. |
| 30 | ~~Score the denoised half against B, not the master~~ | **DONE, and the leak was not the reason.** Rescored against the independent half, the leak is only 0.011 of correlation (0.996 to 0.985) and 0.002 of amplitude, and the verdict is unchanged: every model spends 21-32% of faint-star amplitude to remove 17-26% of the noise. Two metrics also stop working at that depth: structure correlation spans 0.005 across every row incl. the raw half (saturated, cannot rank), and the fabrication count flips direction a third time because the master-at-8-MAD truth mask sits BELOW a half's own 5-MAD bar, so its 76.7 floor is real faint stars and models beat it by erasing them. `n2n_halfscore.py`. |
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
  v14           band conditioning + the half-pair retest; the gate's transfer failure is here
Training cache  C:\tianwen-scratch\n2n-ds                 (1200 cells, 11 slots incl. half pairs)
Scratch         C:\tianwen-scratch                        (NVMe; D: is USB HDD at 37 MB/s)

Re-render the dataset report (no archive scan, works with D: unmounted):
  tianwen dataset report --out D:\Astro-Dataset\2025-2026-darkscaled
```

The N2N scripts live in `D:\Astro-Dataset\n2n-smoke\v14\scripts` (the newest copy; `v10\scripts` is
the pre-band-conditioning snapshot). The reusable half is
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
