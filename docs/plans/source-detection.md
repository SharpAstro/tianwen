# Source detection in `TianWen.Lib` (the photutils shape)

Written 2026-09-14, from the deconvolver campaign's non-stellar clause and the standalone-library
goal ([astropy-parity.md](astropy-parity.md)). The library could find stars (`Image.FindStarsAsync`,
a box per star, a stamped disc per mask) and could remove a smooth background
(`ClassicalBackgroundExtractor`), and it could do nothing with a source that is not a star: no
segmentation, no per-pixel sky and noise, no structure mask that a consumer could ask for, no
measurement of extended detail. Photutils has `Background2D`, `detect_sources`, `deblend_sources`
and a `SegmentationImage`; this plan adds their shapes as pure image-plus-numbers classes under
`TianWen.Lib/Imaging/Sources/`, with no app dependency.

## Why now

- The deconvolver's readouts on real frames measure stars against a sharper half of the night and
  had no instrument for the nebula. The nebula clause of the user's goal ("non-stellar detail more
  visible") was unmet by every arm and could not be told apart from a kernel over-read without a
  structure mask and an extended-object measurement
  ([deconvolver-training.md](deconvolver-training.md), "The Bubble Nebula").
- The per-window kernel the runtime path needs wants the local noise and a star list per window,
  which is a background map and a star mask.
- The background fit's structure classifier (the one-pixel high-pass in `RobustBackgroundFit`) was the
  seed of a segmenter and was private to the fit.

## Phases

Tracked by #898.

| Phase | What | Status |
|---|---|---|
| P0 | This plan; the gap rows in the astropy-parity index; a summary row | DONE 2026-09-14 |
| P1 | `BackgroundMap`: mesh of sigma-clipped cells (median, 1.4826 MAD rms), neighbour fill of invalid cells, odd median filter over the mesh, bilinear to the pixel, exact zero as no data, an exclusion mask for a second pass | DONE 2026-09-14, 2 tests |
| P2 | `SourceSegmentation`: threshold over the map on a smoothed sky-subtracted plane, 8-connected union-find labelling, minimum area, watershed deblend from saddle-separated peaks, `Segment` record with a compact flag, `SegmentationMap` with star / structure / sky masks as `BitMatrix`; two background passes with a low-sigma mask between them | DONE 2026-09-14, 3 tests |
| P3 | `EdgeSpreadProfile`: the extended-object measurement, a segment's boundary read as a line-spread function (the Bubble rim readout, `training/denoise/n2n_rim_readout.py`, ported and generalised from a fitted circle to the segment's own boundary) | DONE 2026-09-14, 5 tests: rims of 5.0 and 9.0 px against 5.3 and 8.1 expected, edges 4.0 and 8.0 against 3.8 and 7.7 |
| P4 | A CLI verb (`tianwen image sources`) printing the segment table and writing the label map and masks, and the deconvolver's real-frame readouts taking their star and structure masks from here | Verb and sidecars DONE 2026-09-14 (`SourceDetectionWriter`, 10 tests, the Bubble run below); the readouts' mask intake is on the deconvolver branch (PR #244) and open |
| P5 | Aperture and segment photometry on the `Segment` records (the `photutils.aperture` half), and the star detector's list cross-matched to segments so one frame has one source catalogue | NOT STARTED |

## Design decisions, each with the measurement behind it

- **The detection threshold is in the UNSMOOTHED noise's sigmas, applied to the smoothed plane**
  (photutils' `detect_threshold` on the data, thresholding the convolved data). Scaling the threshold
  down by the smoothing kernel's noise gain, so that "3 sigma" meant 3 sigma of the smoothed noise,
  read 88 segments on a synthetic frame with six sources: correlated noise clusters past any pixel
  floor at that level. With the convention as photutils has it the same frame reads the six sources
  and a handful of wing islands.
- **Peaks and saddles are read on the SMOOTHED plane.** On the raw plane every noise bump on a
  nebula's surface is a strict 3x3 maximum over 5 sigma, and the watershed cut a 15-sigma nebula
  into twelve pieces.
- **The saddle fraction is 0.7**, between the star deblender's 0.85 and a first guess of 0.5: a 0.3
  and 0.2 pair 6 px apart at 1.5 px sigma dips to 0.62 of the fainter peak and must split (0.5 kept
  it whole); a 4-sigma knot on a 20-sigma nebula dips to about 0.8 and must not (0.85 would carve it
  off).
- **The second background pass masks everything over ONE sigma on the smoothed plane, not only the
  detected segments.** Masking the segments alone left the nebula's faint wing in the mesh, which
  lifted the sky under it and cut the nebula at its own level into pieces of 400 to 1300 px; with
  the low mask the same nebula is one segment of 14,829 px, its 3-sigma contour.
- **Compact means the 5x5 core holds half the flux and the area is under 400 px.** A star's core
  fraction reads 0.79 to 0.82 on the synthetic frames, a nebula's 0.00 to 0.05, so the line has room
  either side; it is a heuristic and says so. The star detector remains the instrument for a star's
  width and profile.
- **`BackgroundMap` is not `ClassicalBackgroundExtractor` and does not replace it.** The extractor
  answers "what smooth model do I subtract" and is built to ignore a nebula that fills the frame; the
  map answers "what is the sky and its noise here" and follows structure the extractor must not. A
  consumer wanting the sky under a source asks the map; one wanting a flat frame asks the extractor.
- **Islands at a wing's threshold are expected**, a handful of 5 to 15 px segments where a nebula's
  faint skirt crosses 3 sigma; photutils reports them too, and the tests filter on area rather than
  pretend they are not there.

## Read on real masters (2026-09-14, `SourceSegmentationProbe`, opt-in by `TIANWEN_SOURCES_FITS`)

| master | size | map | segmentation | segments | compact / extended |
|---|---|---|---|---|---|
| Bubble Nebula (200P, 0.51 arcsec/px, Siril stack, flattened) | 3840 x 2160 | 60 x 34 cells, 1.2 s | 4.9 s | 2,558 | 2,261 / 297 |
| Statue of Liberty Nebula soft half (SH61, 2.9 arcsec/px) | 3173 x 3144 | 50 x 50 cells, 2.1 s | 10.6 s | 35,001 | 34,011 / 990 |

Two things the real frames taught that the synthetic ones could not:

- **A bright star's skirt and diffraction spikes are not compact by core fraction.** On the Bubble a
  7,000-sigma star covered 9,546 px with 10 percent of its flux in the 5x5 core and read as
  structure. The second criterion, the peak over the segment's mean pixel (stars 30 to 200 there,
  nebula pieces 2 to 6, the line at 10), moved it and a 3,100 px twin to compact; the largest nebula
  pieces stayed at 1.8 to 6.5.
- **A dense field at 3 sigma joins into segments of a hundred thousand pixels**, and the deblend's
  saddle test over every pair of their thousands of maxima ran eight minutes on the Statue master
  (the first draft of this paragraph blamed it for ending the E3.4d seed 1 training at 10:17 by taking
  the machine's memory; the user asked how a probe kills a 64 GB machine, and the event log answers
  that it did not: the trainer's log went silent at 10:12:58 and the System log carries an
  `nvlddmkm` error 153, the NVIDIA driver, at 10:17:18, the second the launcher wrote "failed", while
  the probe's own end at the same minute was its 500 s wrapper expiring. A GPU driver event, not the
  library's memory, and not proven to be the probe's doing at all). The cap of 64 peaks per segment brought the frame
  to 10.6 s. What remains is a known limitation: on the Statue the four largest "compact" segments are
  78,000 to 117,000 px, a bright star with the faint field attached to it through the 1 sigma
  mask's continuity, and neither class fits them. **Measured the same afternoon** (`SweepThresholdsOnARealMaster`,
  the Statue soft half, the star detector's 11,237 stars at SNR 10 as the external check):

  | sigma | min px | segments | compact / extended | largest | p99 area | detector stars inside a compact segment |
  |---|---|---|---|---|---|---|
  | 3 | 5 | 35,001 | 34,011 / 990 | 117,116 | 238 | 11,212 (99.8 %) |
  | 4 | 5 | 43,048 | 41,571 / 1,477 | 75,335 | 219 | 11,179 (99.5 %) |
  | 5 | 5 | 49,614 | 47,763 / 1,851 | 8,019 | 379 | 11,119 (98.9 %) |
  | 5 | 9 | 44,520 | 42,671 / 1,849 | 8,019 | 446 | 11,064 (98.5 %) |

  Two readings. The segmentation and the star detector agree on stars to a percent or two at every
  setting, so the compact class is what it claims. And the field-sized blobs are a THRESHOLD effect,
  not a deblend one: at 5 sigma the largest segment is a bright star with its spikes (8,019 px) and
  the count RISES, the blobs having come apart into their stars. Hence the rule now in `Detect`: a
  segment over `CrowdedArea` (20,000 px) holding at least `CrowdedPeaks` (32) significant maxima is a
  field, not a source, and the detection re-runs one sigma higher, at most `CrowdedRetries` (2) times;
  a giant segment with few maxima is a nebula and is left alone. The maxima count is the deblend's,
  recorded per segment as `Segment.PeakCount` (a first draft keyed the rule on the compact flag, and a
  synthetic crowd whose bright star was only 25 times its neighbours read as extended and slipped
  it). The minimum area is not the lever (5 to 9 moves the count by a tenth and the largest not at all).
  With the rule on, the Statue master reads 49,614 segments in 25 s (three detections, the retries
  landing it at 5 sigma), the largest extended segment 800 px and the field-sized blobs gone; the cost
  of a crowded frame is the extra passes, which a caller can cap with `CrowdedRetries`.

## Cost (BenchmarkDotNet, `SourceSegmentationBenchmarks`, ShortRun, 2026-09-14, a GPU trainer running beside it)

Synthetic frame, one star per 64 by 64 cell and a 40 px nebula, 64 px cells:

| stage | 2048 square (4.2 Mpx) | 4096 square (16.8 Mpx) | allocated at 4096 | before the allocation work |
|---|---|---|---|---|
| `BackgroundMap.Estimate` | 186 ms | 728 ms | 116 KB | same |
| `SourceSegmentation.Detect` (two passes, deblend) | 590 ms | 2.35 s | 420 MB | 2.56 s, 601 MB |
| star + structure + sky masks (margin 3) | 21 ms | 87 ms | 12 MB | 715 ms, 90 MB |
| `EdgeSpreadProfile.Measure` (36 sectors) | 18 ms | 31 ms | 2.3 MB | same |

The map is cheap and allocates nothing to speak of. The detection's planes (the sky-subtracted plane,
its smoothed copy, the smoothing scratch, the threshold flags, a rank scratch: 24 bytes a pixel) are
allocated once for both passes since the second commit, and the deblend's per-segment dictionary is
that rank scratch; the labels are the map's own and a fresh array per pass. What is left, 420 MB at
16.8 Mpx, is those planes themselves; taking them from a pool across calls would remove it, and is not
worth doing until a caller runs the detection in a loop. The masks dilate on the `BitMatrix` words
(`BitMatrix.DilateSquare`, pinned against the boolean-plane dilation), eight times faster and a
seventh of the allocation.

## The verb and its sidecars (2026-09-14)

`tianwen image sources <frame> [--channel N] [--sigma 3] [--min-pixels 5] [--no-deblend] [--block-size 64]
[--margin 3] [--top 10] [--maps] [--csv] [-o DIR]` runs the map and the segmentation on one channel (the
reference star channel by default), prints the summary and the largest segments of each class, and on
request puts the detection on disk through `SourceDetectionWriter`, beside the frame or in `-o`, every
file named by a suffix on the frame's own name (the `.rejection.fits` convention) and saying what it
holds in a `MAPKIND` card:

| file | contents | container | `MAPKIND` |
|---|---|---|---|
| `<stem>.labels.fits` | segment label per pixel, 0 is sky | 32-bit integer (new in the FITS writer; a float plane is exact only to 2^24) | `LABELS` |
| `<stem>.starmask.fits` | compact segments plus the margin | 8-bit 0 / 1 | `STARMASK` |
| `<stem>.structmask.fits` | extended segments | 8-bit 0 / 1 | `STRUCTMASK` |
| `<stem>.skymask.fits` | nothing within a margin of any source | 8-bit 0 / 1 | `SKYMASK` |
| `<stem>.background.fits` | the mesh background at the pixel, frame units | float | `BACKGROUND` |
| `<stem>.rms.fits` | the mesh noise at the pixel, frame units | float | `RMS` |
| `<stem>.sources.csv` | one row per segment in label order: position, sky position when the frame carries a solution, peak and its signal over the local noise, flux, box, shape figures, `peak_count`, `compact` | text | |

Each sidecar carries `SWCREATE = TianWen.Imaging.Sources` and `IMAGETYP = SOURCEMAP`, so a folder scan
never takes a mask for a light, and the frame's WCS, so a mask solves where its frame solves. On the
Bubble master (3840 x 2160): 2,547 segments in 4.5 s, the label map 33 MB, each mask 8.3 MB, the table
266 KB; read back with astropy the masks are disjoint, every labelled pixel lies in the star or the
structure mask, and the label map holds 2,547 distinct labels. Two things a reader of the outputs should
know: `peak_count` is the PARENT's count of significant maxima before the deblend split it, so the pieces
of one crowded parent all carry the same number (the four largest extended segments on the Bubble all say
198, one parent); and the sky columns are empty on a frame whose WCS is a target hint (CRVAL and CTYPE
with no CRPIX or CD matrix, as a flattened export carries), not a fault of the table.

The map now says which sigma it was detected at (`SegmentationMap.ThresholdSigma`), and the verb prints
it beside the option when the crowded rule raised it. On the SHARP Statue master (3035 x 3031, the
E2.10b pair's sharper half) the rule lands at 5 sigma, 70,318 segments in 25 s, the largest extended
segment 300 px; and the star mask at a 3 px margin covers 76 percent of the frame, 90 percent of the
readout's 1024 px crop, with the structure mask at 2.4 percent of it. A consumer wanting sky pixels on
a dense field at the SH61's scale gets a tenth of the frame from these masks, and one wanting the
nebula gets small pieces: on such a field the masks are for excluding stars, not for finding structure.

## Traps

- `BitMatrix` indexes `[row, column]`; a segmentation map indexes `(x, y)`. `SegmentationMap.LabelAt`
  takes `(x, y)`; the masks are `BitMatrix` and take `[y, x]`.
- The mesh follows structure larger than about a cell. A nebula of 40 px sigma in 64 px cells survives
  only because of the second pass; a nebula that fills half the frame is the extractor's problem.
- `Image` planes must be hoisted once (`GetChannelSpan`), never read through the indexer per pixel;
  both classes take a `ReadOnlySpan<float>` plus width and height for that reason and offer an
  `Image` overload only as a convenience.
