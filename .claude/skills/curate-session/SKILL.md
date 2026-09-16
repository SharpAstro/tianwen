---
name: curate-session
description: File a capture session from D:/Astro-Pics or D:/Astro-Unsorted into D:/Astro-Organized so a bake can use it, including backfilling the filter identity by measurement when no FILTER card exists. Use when the user wants a session organized, asks what is missing from the archive or the bake, asks which filter a session was shot through, or asks whether a calibration set is safe to use.
---

Usage: `/curate-session <session path>`, or with no argument to report what is unreachable first.

## The four stores are TIERS, and only two are bake roots

| store | what it is | a bake root? |
|---|---|---|
| `D:/Astro-Pics` | the canonical RAW archive. **Never written to** | **no** |
| `D:/Astro-Unsorted` | swept in, uncurated: original names, no measured filter, no calibration map | yes |
| `D:/Astro-Organized` | a verified byte-for-byte COPY, curated | yes (its three subtrees) |
| `D:/Astro-Dataset/<bake>` | the bake OUTPUT: session masters, tiles, stats | it IS the output |

`Astro-Dataset` is **derived**, not a sibling: delete a bake and re-run it. `Astro-Organized` is the
opposite and encodes decisions a script cannot re-derive (which filter, which calibration set), which
is why it is copied rather than generated.

**Curating moves a session Unsorted -> Organized and improves what the bake KNOWS about it. It does
not change whether the bake SEES it** (both are roots). A session still in `Astro-Pics` is invisible
to every bake whatever its quality.

Layout, and why: `_provenance/README.md` in the archive. Bake invocation and the
`--archive-root` trap: the `reference_archive_ingest_roots_and_layout` memory.

## Step 0: what is actually unreachable

**Compare by frame NAME or INODE, never by folder name.** Organized renames folders (filter-first,
target-first) and Unsorted keeps the originals, so a directory comparison reports false gaps.

**Unsorted is mostly HARD LINKS back to Astro-Pics, and Organized is a real copy.** Measured
2026-09-16: 2,330 of 3,000 sampled Unsorted frames have `st_nlink > 1` with none pointing inside that
tree, while Organized has zero. So a frame is already reachable if **either** its inode appears in a
root (a link) **or** its `(name, size)` does (a copy). Inode alone under-reports Organized; name
alone is weak on its own, because master names collide across tools (three different
`masterBias_BIN-1_3008x3008.xisf` sizes exist on D:).

This also means **Unsorted costs almost no disk** and a "copy" into it is a link, so the usual
size arithmetic does not apply there.

**Track inodes while planning a copy and skip repeats** (`organizeC.py` / `organizeD.py` record a
`dedup-skip` row rather than copying the same file twice under two names). A session reached through
two paths is normal here, not a sign of a mistake.

**Measure the gap over FITS ONLY, or it inflates.** Raw frames in this archive are always `.fits`;
`.xisf` is a PixInsight intermediate and nothing reads it (no `.xisf` support exists). Counting them
put the unfiled total at 69,005 frames / 1.63 TiB where the real figure is **62,846 / 1.15 TiB**, and
made `2025/2025-10` look like 2,365 unfiled frames when it is essentially complete: 1,096 of its 1,195
FITS are filed and the remaining 99 are stacked outputs, `MASTER*`, `BADPIXELMAP`, `CALIBRATED-LIGHT`
and zero-exposure aborts. The ledger's `kind` field separates them (`fits-data` against
`whole-file`).

**`D:/Astro-Pics/Unsorted` is NOT `D:/Astro-Unsorted`.** The first is a subfolder of the raw archive
and holds 2,103 FITS; the second is a bake root. A survey that walks only the year folders
(`2024/`, `2025/`, `2026/`) misses it entirely, which is exactly what happened on the first pass.
`Stash/` holds none and `Capture/` holds 26.

**Two kinds of folder must stay out, and neither is obvious from its name:** a `PROC/` folder is
processed output, and `2026-02-20 BAD LIGHT EXAMPLES` is deliberately bad data kept as a reference.

## Step 1: the filter, when nothing states it

**Most sessions here have NO `FILTER` card at all.** Check first; a card saying `RGB`, `LUM` or
`all channels` is a processing mode, not a filter, and must not be taken as one.

The method, and the reasoning behind it, is `docs/plans/filter-inference.md`. In short: a filter's
passband is imprinted on the **raw, undebayered** frame, and **background B/G is the discriminator**
while R/G is useless (the populations overlap). Corroborate with the channel ratios of the session's
own FLAT set, which carries the passband with none of the sky's confounds.

Four traps, each of which produced a confident wrong answer before it was caught:

- **Read `BAYERPAT` per frame; never assume `RGGB`.** SVBONY writes `GRBG`, QHY writes `RGGB`.
  Forcing the wrong one silently samples two greens as "R" and "B", and the tell is `R/G` and `B/G`
  coming out equal to four figures.
- **The reference bands are IMX533-derived and DO NOT transfer to another sensor.** A different CFA
  samples the same passband differently. On a new body the ratios can say *whether one filter ran all
  night*, which is worth having on its own, but they cannot NAME it.
- **A bias is required, and its absence is why whole groups are parked.** The raw ratio overlaps
  between families; the correction is what separates them. If no bias exists for the rig at that
  gain, the session stays parked rather than getting a guessed verdict.
- **A filename tag beats a measurement it does not contradict.** Several sessions carry the filter in
  a frame name (`..._0003_LPS.fits`) or a marker file (`USING_IDAS_LPS.txt`). Use it as the identity
  and use the measurement to confirm one filter ran the whole night.

## Step 2: the slug must RESOLVE, and shorter is more dangerous

The bake reads the filter off the Organized directory name, so **the slug decides whether the session
gets an SPCC curve**. Check it before creating the folder:

```
dotnet test TianWen.Lib.Tests -c Debug --filter "FullyQualifiedName~SpccReachabilityProbe" --logger "console;verbosity=detailed"
```

**A tag that says LESS resolves WORSE**, which inverts the usual instinct about verbose names.
`LPS` and `IDAS` each resolve to nothing (a brand or a family alone must not answer), while
`IDAS-LPS-D3`, `IDAS LPS D3` and `LPS-D3` all resolve to `IDAS_LPS_D3`. A one-sided token difference
still matches, so writing the full name costs nothing. **Add any new slug to that probe's list**, so
a later rename that breaks resolution is visible instead of silent.

## Step 2b: a MONO session is a different problem, and a short slug is fine there

A mono frame has no `BAYERPAT`, so there is no CFA to read a passband off and **the pixel method of
step 1 does not apply at all**. The path tag, a `FILTER` card if one exists, and the owner are the
only evidence.

**And the short-slug danger of step 2 does not apply either.** A bare `LPS` is dangerous because it
resolves to no curve and silently costs a colour calibration. Neither half of that reaches a mono
session: SPCC is broadband-only by design, and a mono frame has ONE channel, so there is no colour to
calibrate and no curve to miss. `Ha` and `Luminance` therefore resolve to NO MATCH and that is
correct, not a gap -- the database holds **no standalone Ha, OIII, SII or Luminance curve at all**
(183 curves, all broadband families or pre-convolved Canon/Sony combinations). The tag's job here is
POOL GROUPING, keeping a 30 s Ha run out of the same training population as a 60 s luminance run,
and for that it only has to be true and consistent. Brand-qualify later if the filter is identified.

**For OPTICS, neither the folder nor the header is authoritative: SOLVE IT.** The luminance
session's folder says `FMA180` while all 247 of its lights carry `TELESCOP = WO RC51` and
`FOCALLEN = 250.0`, and the header is the one that is wrong. `tianwen solve` measures **4.349
arcsec/px**, which on the ASI1600MM's 3.8 um pixels is **180.2 mm**, against the 3.135 arcsec/px
that 250 mm would give. The Ha session beside it measures 4.347, so the two are the same train.

That is the rule `CLAUDE.md` already states ("a declared pixel scale beats `FOCALLEN`, which is only
a hint") reaching its limit: with **no** `PIXSCALE` card, deriving the scale from `FOCALLEN` merely
restates the card, so the sky is the only independent arbiter. **Do not conclude "different optics"
from a `TELESCOP` or `FOCALLEN` card alone**, because a stale capture profile writes a scope that was
swapped out. A blind solve costs seconds and settles it; this one reversed the conclusion.

## Step 3: is the calibration actually safe?

`CalibrationResolver` scores metadata only (gain, offset, exposure, temperature, instrument, date
distance) and **never reads a pixel**. The date-distance score is a PROXY for drift. Measure instead,
three one-pass numbers:

| number | what it says |
|---|---|
| `median(dark - bias)` | what the dark carries beyond the offset. Zero means the dark IS the bias |
| fraction of `light - dark` below zero | over-subtraction, the ONLY destructive direction |
| p0.01 of that residual | the headroom left |

**The test is one-sided and the verdict must say so.** It rules out an offset ABOVE the lights',
which clips faint signal irrecoverably; it cannot certify one below, because the floor may be sky.
That asymmetry is why it is worth taking: under-subtraction only leaves a pedestal, which background
extraction removes anyway. A measured verdict beats a date: one set 184 days old passed cleanly
(0.000% negative over 11.6M px, +524 ADU at p0.01) where the date alone would have refused it.

## Step 4: copy, in the proven shape

Model the script on `_provenance/organizeD.py` (or `organizeC.py`, the longer precedent). Non-negotiable:

- **READ-ONLY on the source.** Everything under Organized is a copy; the raw archive is never written.
- **Dry-run by default**, `--apply` to execute. Print the destinations and the per-kind counts.
- **Refuse before writing** on a destination collision, on an existing destination, on an unexpected
  exposure, and on a frame whose camera or readout mode does not match.
- **Declare the exposures a folder may contain.** A folder name states a kind and cannot be trusted
  to hold only that kind: one folder called `DARK` held dark-flats, another held daylight frames at
  +22 C, and a third (`2026-08 SV545`) held 10.10 s frames typed `DARK` that are the dark-flats for
  the 10.10 s flats beside them. Grouping on `(date, exposure)` and declaring the expectation makes
  an unlisted exposure REFUSE rather than land somewhere its name misdescribes.
- **Exclude what is not a frame, and none of it is obvious from a name.** `IMAGETYP` of
  `MASTERBIAS` / `MASTERDARK` / `MASTERFLAT` / `MASTERDARKFLAT` / `BADPIXELMAP` are derived, and a
  `LIGHT` whose `EXPTIME` is far above the session's sub length is a STACKED OUTPUT with the
  integration time in that card (5820 s = 97 x 60 s), sitting beside the subs it was made from.
- **A SharpCap frame name is unique only within a capture RUN.** It numbers from `frame_00001` and
  restarts for the next run, so a night with two runs (`10_29_51Z` and `12_30_55Z`) holds two
  DIFFERENT frames called `frame_00001.fits`, an hour and a half apart. Filing them into one night
  folder by their own names collides, and the content check refuses it correctly. Prefix with the run
  (`10_29_51Z_frame_00001.fits`), which is unique by construction and keeps the original readable.
  **The run directory is not always the parent**: the subs sit under `<run>/rawframes/`, so walk UP
  the path components for the first that matches, rather than taking the immediate folder. N.I.N.A.
  names already carry a timestamp and need none of this, and a single-run SharpCap session never
  shows the problem, which is exactly why it will surface on a later session rather than the first.
- **Resolve a destination collision by CONTENT, not by inode.** Two sources landing on one
  destination are usually the same frame stored twice at different inodes, which no link check sees,
  because copying is how this archive duplicates. Hash both: identical means keep one and record the
  rest; differing is a real refusal, since one destination cannot hold two different frames.
  **The folder is what is wrong in that case, not the header.** In `2026-08 SV545`, 240 frames under
  `Lobster Nebula/LIGHT/` carry `OBJECT = Small Magellanic Cloud` with the SMC's RA and Dec, and are
  byte-identical to frames in the `SMC/` folder beside them. Splitting on `OBJECT` files them
  correctly and the duplicates collapse; trusting the folder would have filed 240 SMC frames as
  Lobster.
- **sha256 every copy** against its source and write a manifest.

Destinations:
```
lights/<camera>/<filter>/<target>/<night>/
flats/<camera>/<filter>/<the flat's OWN date>/FLAT|DARKFLAT/
calibration/<camera>/BIAS|DARK/<date>-g<gain>-o<offset>-t<temp>[-e<exp>s]/
```
A flat set is filed under **its own** date, never the session's: a shared set can only live in one
folder, and filing by session date left 10 of 18 sessions with no flats folder at all.

**One night can hold several targets.** Split the lights on `OBJECT`, not on the folder. Normalise a
truncated card (`ome Cen Cluster`) in the path and keep the original in the manifest.

## Step 4b: what may be BORROWED from another session, and what may not

A session with no calibration of its own is common here, and the split is by what each frame type
depends on:

| frame | depends on | borrowable? |
|---|---|---|
| BIAS | sensor, gain, offset, (temperature) | **yes**, across optics and across months |
| DARK | sensor, gain, offset, temperature, EXPOSURE | yes at a matching exposure; else scale it, never ignore it |
| FLAT / DARKFLAT | the OPTICAL TRAIN: vignetting, dust, spacing | **NO** |

**A flat from another train is worse than no flat**, because it imposes a vignette the frame does not
have. `Eta Car 24mm LeHance` and `SMC 120s LEnh` are the same camera, the same filter and the same
gain and offset, so the bias transfers cleanly; they are 24 mm and 368.8 mm, so the flats do not.

**A missing dark is not a free pass either.** The ASI585 measures +294 ADU over bias at 120 s and
-10 C, so a 60 s frame on that body carries real dark current and the honest options are scaling the
120 s set or recording the gap. Write what is missing into the map's verdict columns; a blank there
is what tells a later stack why its background looks wrong.

## Step 5: record the association, then the junctions

The calibration association is **many-to-many** (one flat set serves several nights; one night draws
on sets shot weeks apart), so it lives in `_provenance/session-calibration-map.csv` and never in a
path. Write the measured verdicts from step 3 into the `*_verdict` columns rather than "ok".

Then `python _provenance/targetview.py --apply` to rebuild `targets/` (junctions, lights only). If a
filter slug changed, **remove the stale junctions first**: they store an absolute path, and one
pointing at a renamed folder is a broken link that the rebuild will not clean up.

## Step 6: re-bake, and read the gate

Re-run the bake (`reference_archive_ingest_roots_and_layout` memory has the exact invocation and the
scratch-root rule). What passed and what did not:

- `stats/skipped-sessions.jsonl` names each refusal and why (`fewer-than-2-registered` is the common
  one, with per-session counts of `SkippedTooFewStars` / `SkippedNoQuadFit`).
- `session-masters/*.fits` is what survived, one per session per target.
- The master's filename carries the filter tag the bake resolved, which is the cheapest check that
  step 2 worked.

**Curation does not guarantee a master.** A session can be perfectly filed and still fail
registration; those are different gates and the skip file is the one that says which.

## Step 7 (LAST): prune the redundant paths, and only ever a path

Once a session is organized AND validated, the copies it was made from are paths we keep re-walking
and re-ingesting. Dropping them is the final step, never an early one.

**The rule is `st_nlink > 1`, checked at the moment of unlinking, and it is what makes this safe.**
A directory entry may go only while the inode keeps another, so the payload stays reachable by at
least one path no matter what. Applied repeatedly it converges to exactly one path per unique file
and **can never remove the last one**, which is a property of the rule rather than of the bookkeeping
around it. `st_nlink == 1` is an absolute refusal even when a verified copy exists elsewhere.

**Be honest about what it buys: no disk space, and often not even a shorter bake.** Unlinking one of
several hard links returns nothing; the extent is released only when the final link goes, and this
rule forbids that. Worse, measured 2026-09-16 over the 6,467 eligible paths, **6,408 have their twin
inside `Astro-Pics` itself** (a dated session folder and a project folder such as
`Vela SNR Mosaic Project` holding one file twice). Pics is not a bake root, so removing those changes
nothing about what the bake walks. It removes duplication inside the raw archive and speeds up
inventory scans, which is worth having, but it is tidying rather than saving.

**A path with `nlink > 1` whose twin you cannot LOCATE is not eligible.** 59 of those 6,467 have no
twin in Pics, Unsorted or Organized, so the other link is in a tree that was not scanned
(`Astro-Processing`, or the `C:/temp/astro` working copy). "The payload survives" is only true when
you can say where, so find the twin or leave the path alone.

The earlier pass's figure is the one to quote: of an apparent 486.1 GB reclaimable, **380.9 GB was
hard links (deleting a name frees nothing) and only 71.9 GB is genuinely stored twice**. Expect the
same 7x gap whenever a dedup report is read without splitting links from copies.

Prior art is `_provenance/deletion-candidates.csv`, 9,009 rows from an earlier pass, each carrying
`archive_path`, `archive_inode`, `link_count`, `represented_by_rel` and `sha256_current_tree`. That
shape is the one to keep: the row records WHICH Organized file represents the path and what its
content hashed to, so the decision is auditable after the fact.

Required before unlinking anything:

- `st_nlink > 1` **re-checked live**, never trusted from the CSV. The recorded count ages: an earlier
  prune, or any relink, moves it.
- The `represented_by_rel` file exists in Organized, and its sha256 still matches. A copy that has
  been touched since is not a representative.
- **Never delete inside `D:/Astro-Organized`.** That is the curated tier; if something there is
  wrong, fix the curation instead.
- Write a deletion log as you go. **A delete has no undo record**, unlike the move plan's
  swap-the-columns trick, so the log is the only account of what happened.

**The user runs the destructive script.** Produce it, dry-run it, show the counts and a sample, and
hand it over rather than executing it. Same rule as any destructive operation on live data.

## The ledger, and what it is not

`tools/astro-digest-store.py` maintains **`D:/Astro-Reports/digests.jsonl`**, one record per path:
`{path, size, mtime, dev, ino, nlink, digest, kind}`. It is keyed per INODE so a hard link is never
re-hashed, it is resumable (an unchanged size and mtime is not re-read), and FITS are digested over
the DATA SECTION only, matching `ContentDigest` / `StackManifest.DigestData`. Last full pass: 97,706
records, 78,037 distinct inodes, 1.74 TiB unique.

**It records `path`, so the folder structure is preserved as metadata** and a tree can be described,
compared or rebuilt-in-name from the ledger. Refresh it before and after any prune: the diff is what
turns "files vanished" into "these 6,408 paths were removed on purpose and their payloads are still
reachable at these inodes".

**It is an integrity ledger, not a backup.** It can prove a file is intact, prove two files are the
same bytes, and say exactly what was lost. It cannot give the bytes back.

**On a single disk, state the real risk rather than implying a false one.** Two copies on one drive
protect against an accidental delete, a bad write and a bad sector in one extent. They do not protect
against the drive, the controller or the filesystem going. So the redundancy argument against pruning
a verified duplicate is weaker than it looks, and the redundancy argument for getting the
irreplaceable subset onto any second medium is stronger than it looks. The irreplaceable subset is
raw lights that exist in no other tree, not the derived masters and not the tiles, both of which a
re-bake reproduces.

## Running this as grunt work (Sonnet / Haiku), and where to stop

Steps 0, 4, 5 and 6 are mechanical and delegate well: indexing, diffing, copying under the refusals
above, appending a CSV row, rebuilding junctions, reading the skip file. The dry-run and the sha256
manifest are what make that safe, so **never skip the dry run to save a step**.

**Stop and report rather than deciding, in exactly these cases.** Each one has already produced a
confident wrong answer in this archive:

1. **The filter cannot be named** (no card, no filename tag, no bias for the rig at that gain, or a
   new sensor whose bands are not the IMX533 ones). A guessed filter is worse than a parked session,
   because it silently mixes populations in a training pool.
2. **A new filter slug is needed.** It has to be checked against `SpccReachabilityProbe` and added
   to it, and the full-versus-short choice is not guessable.
3. **The calibration measurement fails or is ambiguous** (any over-subtraction at all, or no bias to
   measure against).
4. **A folder holds an exposure the plan did not declare.** That is the refusal working; it means the
   folder's name misdescribes part of its contents and a human has to say what those frames are.
5. **Anything would be written outside `D:/Astro-Organized`**, or a destination already exists with
   different content.

Everything in that list is a judgement call with a measurable cost. Everything else is copying.
