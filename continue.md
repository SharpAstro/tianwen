# continue.md

Handoff rewritten **2026-08-17 (evening, peak-hours shutdown)**, branch **`feat/ai-enhancements`**.
Supersedes the 2026-08-14 version entirely (that one described the `feat/detection-purity-probe`
wave: the bake, the N2N experiments and their retractions; all of that is merged history now,
recorded in `docs/plans/ai-denoise-deconv.md` and the memory notes).

## TL;DR

Nothing is running. The branch is an accumulating AI-enhancements wave, **all pushed**, one PR at
the end of it (rebase-merge, one-PR-in-flight rule). Every commit below went through a green full
suite (~5.5 min, 4,468+ tests). Two user questions are answered but have follow-ups waiting on a
human: the PixInsight-launch mystery (needs a concrete sighting) and whether to env-gate the
RC-Astro integration tests.

## The wave so far (this branch, newest first)

| Commit | What |
|---|---|
| `c4908d62` | `BAYERPAT='VALID'` decodes as ASCOM RGGB base + XBAYROFF/YBAYROFF (MaxIm convention, verified from pixels) + `SensorTypeTests` |
| `b5801f43` | SBFITSEXT IMAGETYP spellings parse ("Light Frame", "Flat Field"): a MaxIm archive used to read as `FrameType.None` and be invisible |
| `44e14ca8` | `tianwen dataset coverage`: per-session calibration coverage TSV + rollup, resolved by the production matcher (task #51) |
| `9d662dd4` | Task #25: calibration epochs (30-day chain gap), time as a tie-breaker (1/yr = 0.1 degC), stacker `MatchMaster` consumes the resolver's gates/penalties, SWCREATE + DATE-BEG/END provenance on masters |
| `d202fe10` | `--require-gain-match` defaults ON everywhere (a wrong-gain dark is rejected, not penalised) |
| `a16f0be8` | Task #22: bad-pixel map from the lights' own registration, UNIONed with the dark map (near-disjoint populations, APP-oracle-validated) |
| `2b98eae2` | SNAPSHOT on masters + SWMODIFY on enhance outputs (provenance cards) |

## Deliverables that live OUTSIDE the repo

- **`D:\Astro-Reports\calibration-coverage.tsv` + `.md`** (2026-08-17): 60 sessions / 7,161 lights
  over `D:\Astro-Pics\2025`+`2026`, NINA lights only, strict gain gate. Headlines: darks 57/60
  (all gain-matched; the 3 misses are the QHY294 g1600 sessions with ZERO candidates), flats
  60/60, **no session in the source archive carries a FILTER header and no sidecar exists** (the
  dual-band identity lives in folder names only; `D:\Astro-Organized` has the tags instead).
  Re-run: `tianwen dataset coverage --archive-root D:\Astro-Pics\2025 --archive-root
  D:\Astro-Pics\2026 --out D:\Astro-Reports --software "*N.I.N.A.*"` (~12 min, USB-disk-bound).
- **QHY294 dark shopping list** (read off the stranded session's own lights): 60 s, gain 1600,
  offset 40, -5 C, bin 1, **"11M MODE"** readout, 4164x2795, ~20-30 subs; plus ~4 s dark-flats
  (their flats metered 4.05 s) or a g1600/o40 bias set. Un-drops 193 lights / 3 targets
  (task #10, spec now precise).
- **Organized-archive gap** (name-diff vs `D:\Astro-Organized`): every non-ASI533/SV605CC session
  is absent -- Helix 317 (ASI585), Rim 264 + Lagoon 56 (ASI585), eta Car LUM 243 (ASI1600MM),
  SW8Q trio 193 (QHY294) = **1,073 dataset lights with no organized counterpart**. SharpCap/EAA
  exclusions are by design. Task #49's re-org is where this lands.
- **`C:\temp\test-data`**: 191 iTelescope frames (M33 LRGB / M42 Ha+OIII / M45 OSC), renamed
  `.fit` -> `.fits`, **all pre-calibrated** (`CALSTAT='BDF'`, float32, `PEDESTAL=-100`) -- never
  calibrate them again. M45 measured **RGGB** from its own CFA subplane medians. Full detail in
  memory note `reference_itelescope_test_data.md`.

## Open threads, in rough order

1. **Finish the wave -> one PR** from `feat/ai-enhancements` (rebase-merge). Nothing blocks it;
   more items can join first.
2. **PixInsight mystery (user-blocked).** Nothing reproducible launches it: rc-astro spawns zero
   children (license probe AND real nxt run), the RC test classes leave the tripwire untouched,
   and a full suite under a 200 ms process watcher saw nothing. PI genuinely ran once at 21:35:24
   during (not provably because of) a suite. **Tripwire:** `LastWriteTime` of
   `%APPDATA%\Pleiades\core-001-pxi.settings`; watcher script in the old session scratchpad.
   Need the user's concrete sighting (what + when) to go further.
3. **Env-gate the RC-Astro integration tests?** Offered (`TIANWEN_RCASTRO_TESTS`, simulator-suite
   pattern) so routine suites stop running real rc-astro (GPU DirectML spikes). User has not
   decided; do not do it unprompted.
4. **CALSTAT / SWMODIFY / SNAPSHOT read guards** (filed with task #11): now more than theoretical,
   `C:\temp\test-data` is entirely pre-calibrated data a stack run would happily re-calibrate if
   matching darks were present.
5. **Filter identity for the dual-band Astro-Pics sessions**: sidecars (`.tianwen-meta.json`) or
   `dataset tag-filter`; the coverage report makes the gap visible (`filter_source=none` on all 60).
6. **Task #37 (mono narrowband end to end)**: the M42 Ha/OIII set is the perfect real fixture, and
   M33 LRGB exercises mono broadband; both now parse correctly.
7. Optional: run `dataset coverage` over `D:\Astro-Organized` for a filter-aware report.

## Task list snapshot (2026-08-17)

Pending: **#10** QHY294 g1600 dark library (spec above) · **#11** bake older years (+ read guards)
· **#15** gradient fields for P5 · **#37** mono narrowband · **#38** organized panel level ·
**#39** record master inputs (partially superseded by #25's DATE-BEG/END + SWCREATE; check before
starting) · **#49** organized re-re-org · **#50** TILEXY at capture.
Completed this session: **#22, #25, #51** (+ the unnumbered gain-default flip).

## Session learnings (the non-obvious ones)

- **MaxIm DL conventions, all hit on real data**: IMAGETYP uses SBFITSEXT names ("Light Frame",
  "Flat Field"); `BAYERPAT='VALID'` asserts existence and the pattern rides on XBAYROFF/YBAYROFF
  against the ASCOM canonical RGGB base (ASCOM's SensorType has exactly ONE Bayer member, so the
  base cannot be anything else); `PEDESTAL=-100` means +100 ADU was re-added post-calibration.
- **A CFA pattern is measurable from pixels**: the two green subplanes' medians match to ~0.2%
  (names the green diagonal), and the brighter remaining subplane is red on any real sky. The
  scratch `fitscheck` tool (old session scratchpad) did this; trivial to rebuild.
- **The `dev/null/` directory at the repo root** was git-lfs hooks written under
  `core.hooksPath=/dev/null` (Go's `filepath.IsAbs` says false on Windows, so it lands relative to
  the worktree). Real hooks in `.git/hooks` were intact; artifact deleted. If it reappears, some
  tool is invoking git with that config.
- **`FormattableString.Invariant` rejects concatenated interpolated strings** (`$"a" + $"b"` is a
  string, not a FormattableString) -- hit twice in one day; write one long interpolation.
- **Process-launch forensics on Windows without admin**: vendor app-data write times are a better
  tripwire than process polling (they catch a launch that happened while you were not looking),
  and a 200 ms `Win32_Process` poll with ParentProcessId capture names the launcher when you can
  watch live. Prefetch needs admin; PI's is `%APPDATA%\Pleiades`.
- **The coverage-report design earned its keep immediately**: putting a candidate COUNT beside
  every resolved-or-not column turns "no dark" into either "shoot a library" (0) or "a gate
  refused them" (>0) with no investigation.

Delete this file when the wave's PR lands.
