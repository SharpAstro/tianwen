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
