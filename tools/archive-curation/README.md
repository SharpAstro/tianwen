# archive-curation

The scripts behind the `curate-session` skill (`.claude/skills/curate-session/SKILL.md`), which is the
METHOD; this folder is what the method was measured and executed with. They were written against the
real archive as it stood in 2026-08 and 2026-09 and lived in `C:/temp/e2`, a session scratchpad and
`D:/Astro-Organized/_provenance` until 2026-09-17. Nothing here is built or tested by `dotnet`, and the
logic is meant to move into `tianwen dataset` verbs over time (task #40); `dataset tag-filter`,
`tag-frame-type` and `relabel-frame-type` are the parts that already have.

**Every script is READ-ONLY on the archive** except `organizeM.py`, `targetview.py` and `finishM.py`,
which are dry runs unless given `--apply` and write only under `D:/Astro-Organized`. Roots and
cache paths are constants at the top of each file (`D:/Astro-Reports/digests.jsonl`, the archive
roots, caches under `C:/temp/e2`), so edit those rather than expecting arguments. The group-named
scripts are the reference for the next session of the same kind: copy, change the constants, keep
the checks.

Needs `astropy` and `numpy`; the renders also want `Pillow`. The solves and star counts call the
Release CLI at its build path (the `TIANWEN` constant), so build `src/TianWen.Cli` in Release first.

## Step 0: what no bake can reach

| script | what it answers |
|---|---|
| `astro-digest-store.py` | maintains `D:/Astro-Reports/digests.jsonl`, the content ledger every other Step 0 script reads (refresh it first; incremental is minutes, cold is hours, and a retag makes it re-read every retagged frame). After a full walk it TOMBSTONES a recorded path that is really gone, names one that is on disk but was not reached, and refuses a root that does not exist |
| `digest_ledger.py` | the one way to READ that ledger: each path's latest record, tombstoned paths left out. A reader that keeps every record calls a withdrawn or deleted file filed (1,300 raw names on 2026-09-25), so every script here imports it rather than parsing the file itself |
| `validate_archive.py` | a per-frame verdict ledger (`C:/temp/e2/archive-ledger.csv`): does Organized hold everything salvageable from Astro-Pics. Written after three surveys gave three answers; each failure mode is a named case in the docstring |
| `gap_from_ledger.py` | what is not yet filed, by CONTENT digest off the ledger (a disk walk missed `D:/Astro-Pics/Unsorted` and undercounted ninefold) |
| `whats_missing.py <dir>...` | for one Astro-Pics session, which frames are missing, broken down by kind |
| `triage.py` | every unfiled session sorted by what filing it would need (known body, filter hint, own calibration) |
| `generic_names.py` | how much "reachable" rests on a generic SharpCap `frame_00001.fits` matched by name and size alone |
| `find_m83.py` | find a target by its `OBJECT` header, one header per directory (the template for any "where is X") |
| `astro-archive-folder-vs-object.py` | how far the Organized folder names disagree with `OBJECT`, read off a bake's scan summary |
| `twins_from_ledger.py`, `prune_audit.py` | where a hard link's twin lives, and what a "drop a path only while `st_nlink > 1`" prune would do. Audits; neither deletes |
| `ctemp_redundancy.py` | how much of `C:/temp/astro` is already on D: by content |
| `astro-archive-dedup.py`, `astro-archive-hardlink.py`, `astro-archive-organize.py` | the 2026-08 BobbyBox-Temp reconciliation (Step 0 of `docs/plans/ai-denoise-deconv.md`): header index, hard-link dedup, organizer |

## Step 0b: the frame type, decided by the pixels and the sky

Run in this order; each writes the cache the next one reads (all under `C:/temp/e2`).

| script | what it does |
|---|---|
| `stage0_types.py` | reads the type card that EXISTS (`FRAMETYP` before `IMAGETYP`, as the reader does) over the ledger's gap and drops products (APP, SharpCap live stacks) |
| `prenina_headers.py` | samples what the pre-N.I.N.A. frames actually carry, per year and camera |
| `stage0_sets.py` | groups the gap into capture sets, to size the pixel pass |
| `exact_exptime.py` | the exact `EXPTIME` per sampled frame (a two-decimal copy turned a 0.61 ms Moon frame into a bias) |
| `classify_sets.py` | the decision: one verdict per capture set from up to three frames, bias by minimum exposure, level against the nearest bias of the same gain and `BLKLEVEL`, flat by level AND shape, light by a solve. `--verdicts-only` re-prints from the caches |
| `hinted_solves.py` | retries the blind-solve misses with a position and a scale |
| `look_stars.py` | star counts and FWHM on the lights no solve could place (the bake registers by quads, not by a solve) |
| `dark_rate_probe.py` | heat or light in a dark: its rate against the camera's own darks, normalised to 25 C |
| `sample_darks.py` | renders suspect darks beside their bias and a light, on a fixed scale and their own stretch |
| `writer-roworder-scan.py` | one frame per leaf directory: which software wrote it, `ROWORDER`, `BAYERPAT` (a row flip re-phases the CFA) |

## Step 1: the filter, when nothing states it

The method and its traps are `docs/plans/filter-inference.md`. A new BODY first gets its own
constants (bias per channel, gain, ADU scale); only then are sessions compared, and only against the
same sensor.

| script | session | what it established |
|---|---|---|
| `sv605-calibrate.py` | SV605CC | a new body's constants before any verdict: bias per channel, gain from flat pairs, ADU scale |
| `sv605-measure.py` | group C, 10 sessions | the canonical per-session pass: measured bias, background B/G on the raw mosaic, flat ratios as corroboration |
| `groupd_filter.py` | group D, QHY294C | the same, once a bias at gain 1600 existed |
| `sv545_filter.py` | group E, QHY294C | same sensor and readout mode as group D, so the ratios compare directly |
| `asi585_adu.py` | ASI585MC Pro | green's multiple-of-16 spacing is quantisation, not a 16x gain (ratio 0.77) |
| `asi585_filter.py` | ASI585MC Pro | every session against the body's own L-eNhance reference frame; B/G separates nothing here, R/G does |
| `groupK_filter.py` | group K, ASI585 | flat ratios and bias-corrected sky, two independent measurements |
| `filter_from_flats.py` | group J, SV605CC | a closer calibration set, and the filter in a manual holder read from the flats |
| `imx294_reference.py`, `imx294_star_colours.py`, `etacar_filter_class.py` | group M, ASI294MC | same-sensor flats, star colours as the one light source constant across nights, and the nebula-over-stars ratio of ratios inside one frame |

## Step 3 and 4b: is the calibration safe, and may a flat be borrowed

| script | what it answers |
|---|---|
| `groupM_measure.py` | the three one-pass numbers (`median(dark - bias)`, fraction of `light - dark` below zero, its p0.01) plus the filter evidence, for one night |
| `flat_age_check.py`, `groupM_dust.py` | has the dust moved between the flat and the lights: where the flat dips, does the light dip too |
| `train_cards.py` | which optical-train cards (`TELESCOP`, `FOCALLEN`) each Organized flat set and light session carries |
| `flat_distances.py` | days from each session's first light to every card-less flat set's span; the measurement behind `CalibrationResolver.UnprovenFlatMaxDays` (14) |
| `asi533_flats.py` | the SharpCap-era ASI533 flat sets the flat-train fix moved, with the cards the resolver sees |

## Step 4 and 5: copy, then record

| script | what it does |
|---|---|
| `organizeM.py` | the current organizer, and the one to copy: dry run by default, exposures declared per folder, collisions resolved by content then refused, SharpCap run prefixes, every copy sha256-verified, a manifest, and `--verify-tags` after a tagging pass |
| `targetview.py [--apply]` | rebuilds the `targets/` junction view over `lights/`; re-run after any filing (junctions store absolute paths) |
| `make_groupM_calmap.py <dark count>` | a `session-calibration-map.csv` row with its measured verdicts, written as a fragment to `C:/temp/e2`; the dark count comes from the coverage matcher, never a guess |
| `finishM.py [--apply]` | appends that row and the `CORRECTIONS.md` section, after copying the map to the next zero-padded `.bakNNN`; idempotent |

## What stays with the archive

`D:/Astro-Organized/_provenance` keeps the per-group organizers (`reorg.py`, `restructure.py`,
`organizeC.py` to `organizeL.py`, `calmapC.py`, `fix-labels-lum.py`) beside the manifests, hashes, maps
and corrections they produced. Those are the record of what was copied where; the scripts here are
how to do the next one.
