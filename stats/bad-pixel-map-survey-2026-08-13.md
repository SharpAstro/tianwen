# Bad-pixel map survey: what APP's maps actually contain (2026-08-13)

Measured by `SuperBadPixelMapProbe` (env-gated, `TianWen.Lib.Tests`) over every Astro Pixel
Processor bad-pixel map archived in `D:\Astro-Pics`, to decide how a per-sensor "super map" should
combine them. Cross-checked against `BadPixelDetection.BuildMaskFromDark` at sigma 8 on
`master_dark_120s_-10C_g121_ZWOASI533MCPro.fits`.

## The headline

**APP's bad-pixel map is not a defect map. At kappa 3.0 it is ~92% per-run noise.** The plan this
survey was meant to serve (OR the archived maps into a per-sensor super map) is wrong as stated, and
the gap it was meant to close is 2.4x, not the 33x the raw counts suggested.

## Inventory

Filter is the filename infix `ZWO_ASI533MC_Pro-3008x3008`, confirmed against the `INSTRUME` header of
every file rather than trusted from the name: all 28 report `ZWO ASI533MC Pro`. The 6
`SVBONY SV605CC` maps are also 3008x3008 (same IMX533 design, different physical sensor) and were
correctly excluded. Identity for a defect map is the SENSOR, never the geometry.

| | count |
|---|---|
| files on disk matching the sensor | 28 |
| exact content duplicates (a run copies its BPM into each output folder) | 12 |
| empty maps (0 flagged, a failed APP run) | 1 |
| **distinct usable maps, 2021-09 to 2026-01** | **15** |

Deduplication is not cosmetic. One map is present eight times across the Vela mosaic panels; counting
it once per copy gives every pixel in it K=8 unaided, manufacturing exactly the "stable across many
runs" signal the analysis exists to detect. The first pass did this and produced a spurious spike of
71,586 pixels at K=8.

## Each map flags ~2.8%, and they do not agree

| | pixels | % of frame |
|---|---|---|
| typical single map | 234k - 289k | 2.6% - 3.2% |
| union of all 15 | 1,803,690 | **19.94%** |
| flagged by exactly ONE map | 1,118,142 | 12.36% (62% of the union) |
| flagged by ALL 15 | 18,393 | 0.203% |

Two independent reads of the same fact:

- Two maps of the same physical sensor 2.3 years apart share **28.6%** of their flagged pixels
  (10.3x chance enrichment, so the shared part is real, but it is a minority of each map).
- Map 15 (2026-01-29) still contributes **56,618** never-before-flagged pixels against map 14 six
  weeks earlier. Sensor aging does not add 56k pixels in six weeks, and defects do not heal.

A plain union would mask a fifth of the sensor.

## The consensus core is real, and our dark independently confirms it

Distribution of our sigma-8 dark-derived mask (7,562 px) across the K buckets:

| K (maps agreeing) | our px | % of ours | size of bucket | share of bucket we confirm |
|---|---|---|---|---|
| 15 (unanimous) | 6,060 | **80.14%** | 18,393 | **32.9%** |
| 14 | 431 | 5.70% | 13,259 | 3.3% |
| 13 | 104 | 1.38% | 5,770 | 1.8% |
| 12 | 109 | 1.44% | 4,350 | 2.5% |
| 0 (in no map) | 141 | 1.86% | - | - |

The unanimous bucket is 0.203% of the frame and takes 80.14% of our detections: a **395x
enrichment**. Our detector and APP's agree strongly about a small population and disagree about
everything else, which is what a real defect set surrounded by noise looks like. The confirmation
rate then falls off a cliff between K=15 (32.9%) and K=14 (3.3%).

Read the cliff carefully in one direction only. It shows K=15 is solidly evidenced; it does NOT show
K=14 is noise, because our mask is one dark at one gain, temperature and exposure and can only see
hot pixels, so it cannot confirm cold pixels, RTS pixels or column defects at any K.

## What this means for the mask

- Current dark-derived mask: 7,562 px (0.084%)
- Unanimous consensus core: 18,393 px (0.203%)
- Their union: 19,895 px (0.220%) - 6,060 are common to both

**Superseded by the fidelity sweep below.** The union framing assumed we could not regenerate the
core ourselves and would have to borrow APP's maps at runtime. Measuring recall against the core
showed otherwise, and the achievable number is better than the 2.4x this section implies.

What this section does correct is the pre-rebake checklist's "even sigma 2 is 15x short", which
measured our mask against APP's 253,249 - a number that is mostly not defects.

## Regenerating the map ourselves: recall against the core

Sweeping sigma and scoring against CORE (the 18,393 unanimous px) and NEVER (the 7,244,374 px no map
ever flagged), on `master_dark_120s_-10C_g121_ZWOASI533MCPro`:

| sigma | flagged | core recall | landed in NEVER |
|---|---|---|---|
| 8.0 (old default) | 7,562 | 32.95% | 1.86% |
| 4.0 | 11,678 | 53.26% | 1.87% |
| 2.0 | 16,570 | 74.73% | 1.93% |
| 1.0 | 21,898 | 85.99% | 1.93% |
| 0.5 | 106,053 | 97.38% | 7.90% |

Contamination is FLAT until the cliff, so the pixels gained by lowering the threshold are ones APP
flagged too. This is largely a THRESHOLD problem, not the population problem the raw counts
suggested.

**But a fixed sigma cannot be the answer.** On `..._g252_...` - same sensor, different gain - sigma 1
flags 1,852,117 px (20.5% of the frame, 59.35% of it in NEVER). Tracing the detector shows a runaway
in the kappa-sigma loop rather than anything about the parameter:

```
iter=0: median=780 mad=4.0 threshold=785.93 added=330,021
iter=1: median=778 mad=2.0 threshold=780.97 added=1,522,096
```

Masking 330k pixels shifts the sample median down and HALVES the MAD, lowering the threshold, which
masks 1.5M more. The mask only grows and the sample only shrinks, so the estimate can only tighten.
The convergence test ("stop when an iteration adds under 0.01%") notices only after the run has
finished consuming. The g121 dark escapes purely by accident: its MAD is 0, so the non-zero-tail
fallback pins the scale at 4.0 where the estimate cannot move.

## The fix, and what it recovers

Estimate the noise scale ONCE (guarded), then walk sigma down against a defect budget. From the same
caller sigma of 8:

| dark | selects | flagged | core recall | contamination |
|---|---|---|---|---|
| g121 | sigma 0.80 | 21,898 (0.242%) | **85.99%** (was 32.95%) | 1.93% |
| g252 | sigma 3.38 | 23,904 (0.264%) | **89.42%** (was 74.77%) | 1.93% |

Both land near an 800 ADU threshold despite different gains, which is the consistency you would want
from one physical sensor. Nothing borrows an APP map at runtime; they are only the yardstick.

Whether this clears the drizzled clusters is still open and needs the A/B re-run; the old sigma-8
mask took 52 clusters to 35.

### The A/B ran, 2026-08-15, and answers half of that

`HotPixelMaskProbe` used to write two masters and say "diff them", asserting nothing. It now asserts,
and the load-bearing assertion is that BOTH runs report `BayerDrizzle`: only the drizzle strategies
read `IntegrationJob.BadPixelMask`, so an AHD fallback (which a too-small `TIANWEN_BPM_MAXLIGHTS`
causes, below the 60 matched frames drizzle needs) shows no difference and the probe would pass by
proving nothing.

On 2025-12-28 Segaull+Thors_Helmet, ASI533MC Pro g121 60s against the 120s/-10C/g121 master dark,
100 lights capped and 90 registered, both runs drizzle: **2119 px changed (0.0077% of frame) in 617
clusters across all three channels, extreme outliers 162618 -> 161334.** So the mask is surgical and
correctly signed.

**That is not the same measurement as the 52 -> 35 above.** This counts clusters in the DIFFERENCE
between the two masters; the 52 -> 35 counts hot-pixel clusters REMAINING in the master. A mask can
change many pixels and still leave the visible clusters standing.

### Resolved: the clusters do clear

Cropping both masters at the largest changes and rendering them side by side under one shared
stretch (`bpm_figure.py`, figure at `C:\tianwen-scratch\bpm-probe\hotpix-before-after.png`), every
cluster present without the mask is absent with it. Not reduced -- absent.

Each cluster is also a SINGLE COLOUR, which is the physical confirmation that these are what we
think: a defect occupies one photosite, so it lands in one CFA channel, and drizzle then scatters it
along the dither path. That is why they appear as ~36x25 px blobs congruent with the session's dither
excursion rather than as dots, and why the union canvas grew by (+37, +27) for the same reason.

Whole-frame isolated peaks above 30 MAD go 12,468 -> 12,247. Most of those are real stars and are
untouched, which is the other half of the result: the mask removed 221 of them and left the rest.

### A reference-free census does NOT work, and how that was established

Verifying the re-bake cannot reuse the A/B above, because between two bakes every commit in between
moved. That argues for a measurement each master answers alone, and the single-colour finding
suggests one: count clusters bright in exactly ONE channel, since a star carries flux in all three.

(An earlier version of this paragraph also claimed drizzle deposition "is not bit-identical run to
run", citing 2.96M of 27.6M px differing between the two A/B runs. **That was an assumption stated as
a fact and it is wrong** -- see the determinism note at the end of this document. Those 2.96M
differences are all mask consequence: masking ~22k input pixels changes the accumulated WEIGHT for
every output cell any of them landed in, across 90 dithered frames, so millions of touched cells is
what a mask on that many defects should produce.)

Calibrated against ground truth -- the pixels the mask actually changed -- it fails:

| hot sigma | quiet sigma | min px | clusters found | recall | precision |
|---|---|---|---|---|---|
| 8 | 3.0 | 3 | 2889 | 0.45 | 0.10 |
| 15 | 3.0 | 3 | 794 | 0.36 | 0.28 |
| 30 | 3.0 | 3 | 411 | **0.26** | **0.39** |
| 30 | 1.5 | 8 | 24 | 0.02 | 0.62 |
| 30 | 1.5 | 14 | 3 | 0.00 | 1.00 |

Recall and precision trade off directly and no setting gets both past 0.5. The physical intuition
was right and insufficient: faint stars are ALSO effectively single-channel at this noise level, and
there are far more of them than there are defects. Run as-is it would have reported 2807 -> 2528
clusters (a 10% reduction) on a pair where the crops show the defects removed outright, and that
number would have been read as "the mask barely worked".

**So there is no census tool in `tools/`, deliberately.** Verify a re-bake from the per-session
`hot-pixel mask: N px (P% of frame)` line the registrar already logs, which is direct evidence the
path ran, and rely on the A/B above for evidence that the mask works. The lesson generalises: a
discriminator built from a correct physical argument still has to be scored against a known set
before it is trusted, and the cheapest ground truth available here was the mask's own diff.

### Two ways this measurement went silently wrong first

Both produced a plausible number rather than an error, which is why they are worth recording:

- **NaN.** A drizzled master carries NaN in zero-weight cells and across the union-canvas margin
  (27,359 px here). `np.median` over that returns NaN, every threshold becomes NaN, and `> NaN` is
  false everywhere, so the figure reported "0 px changed, nothing to show" -- indistinguishable from
  a genuine null. It took checking `max |diff|` and getting `nan` back to see it. Note the same trap
  in C#: `Array.Sort` places NaN FIRST, ahead of negative infinity, so a median read at `length/2`
  of a raw drizzled channel is displaced by the NaN fraction. `HotPixelMaskProbe` now filters to
  finite samples before taking either statistic.
- **Statistic mismatch.** Thresholding the LUMINANCE finds nothing: a single-channel defect is
  divided by three when the channels are averaged while the luminance MAD only falls by about
  sqrt(3), so an 8-MAD defect lands near 4.6 MAD and never crosses. Threshold PER CHANNEL against
  that channel's own MAD, which is what the C# probe does. Matched that way, Python reads 2107 px /
  607 clusters against C#'s 2119 / 617, the residual gap being exactly the NaN-displaced median.

### Note on how this document got re-derived

The APP survey was re-run on 2026-08-15 while clearing the pre-rebake gate list, because the
checklist still asserted "even sigma 2 is 15x short" and that had to be resolved before burning
hours. The re-run reproduced this document's numbers exactly (28 files, 15 distinct after dropping
12 content duplicates, K=15 core 18,393 px, K=1 union 19.935%) -- which is a useful independent
confirmation and was also avoidable: the conclusion was already written down here, four sections up.
**Grep the stats directory before re-running a survey.** The checklist was the stale artifact, not
the measurement.

## Two method notes worth keeping

**Row order was settled empirically, not assumed.** APP only began writing `ROWORDER` around
mid-2024, so 14 of 34 maps carry no card and reach the reader as top-down purely by its default. For
a defect map a silent mirror is catastrophic and invisible: it masks good pixels, keeps every real
defect, and leaves the count entirely plausible. Map-against-map, a 2023 map with no card overlaps a
2025 declared-TOP-DOWN map on 28.62% of its pixels in the same orientation (10.27x chance) against
2.87% flipped (1.03x, chance). The pre-2024 maps are top-down.

**That test works only between sets of similar density.** An earlier attempt compared the
7,562-pixel dark mask against a 253k-pixel map and scored 99.8% one way against 100.0% the other,
concluding nothing: a small clustered set lands inside a 2.8%-dense set whichever way up it is.

## Reproducing

```
TIANWEN_BPM_ROOT=D:/Astro-Pics
TIANWEN_BPM_SENSOR=ZWO_ASI533MC_Pro-3008x3008
TIANWEN_BPM_DARK=D:/Astro-Dataset/2025-2026/masters/master_dark_120s_-10C_g121_ZWOASI533MCPro.fits
TIANWEN_BPM_REPORT=<path>
dotnet test TianWen.Lib.Tests --filter FullyQualifiedName~SuperBadPixelMapProbe
```

## The pipeline is bit-deterministic, and the 2026-08-15 re-bake proved it by accident

A full re-bake was run on 2026-08-15 (commit `fd3c95f1`, 5h 20m, 67/68 sessions, 207,900 tiles) to
clear hot pixels from the training masters. **It produced pixel-identical output to the previous
bake.** Three session masters spot-checked across different cameras and dates: every finite pixel
exactly equal, max |diff| 0.0, NaN masks identical. All 67 files differ by hash, but only in header
metadata (the version stamp); the image data is untouched.

The reason is that the mask was ALREADY ACTIVE in the previous bake. The wiring landed in `cd92de8a`
and the budget walk in `2d1ca980`, both 2026-08-13; the `2025-2026-darkscaled` bake ran at `4bf290d9`
the same day, after both. Between that commit and `fd3c95f1` the only `src/` changes are
`DatasetBuildRunner` timing-store bookkeeping, `StageTimings`, and the `HotPixelMaskProbe` test
itself -- nothing on the image path. So the re-bake could not have changed a pixel, and the premise
behind it ("drizzled hot pixels sit in 45 training masters") described a state that the darkscaled
bake had already fixed.

**The check that would have prevented it takes one minute:** read the previous bake's
`bake-provenance.json` for its commit, then `git log -S` the fix into the file it lives in and
compare dates. The provenance file exists precisely so a bake can be asked which code produced it,
and it was read for its ARGUMENTS while the commit field sat right above them.

Two things worth keeping from it anyway:

- **The pipeline is deterministic to the bit** across a full 5-hour rebuild on a different commit,
  through registration, drizzle deposition, and integration. That is a strong property and it is now
  demonstrated rather than assumed. It also means a future bake CAN be verified by differencing
  against its predecessor: any non-zero pixel difference is attributable to the code that changed.
- **A stale premise in a task list outlives the thing it describes.** Both #20 and #23 still asserted
  the hot pixels were present. Neither was wrong when written.
