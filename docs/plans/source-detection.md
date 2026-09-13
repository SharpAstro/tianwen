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

| Phase | What | Status |
|---|---|---|
| P0 | This plan; the gap rows in the astropy-parity index; a summary row | DONE 2026-09-14 |
| P1 | `BackgroundMap`: mesh of sigma-clipped cells (median, 1.4826 MAD rms), neighbour fill of invalid cells, odd median filter over the mesh, bilinear to the pixel, exact zero as no data, an exclusion mask for a second pass | DONE 2026-09-14, 2 tests |
| P2 | `SourceSegmentation`: threshold over the map on a smoothed sky-subtracted plane, 8-connected union-find labelling, minimum area, watershed deblend from saddle-separated peaks, `Segment` record with a compact flag, `SegmentationMap` with star / structure / sky masks as `BitMatrix`; two background passes with a low-sigma mask between them | DONE 2026-09-14, 3 tests |
| P3 | `EdgeSpreadProfile`: the extended-object measurement, a segment's boundary read as a line-spread function (the Bubble rim readout, `training/denoise/n2n_rim_readout.py`, ported and generalised) | NOT STARTED |
| P4 | A CLI verb (`tianwen image sources`) printing the segment table and writing the label map and masks, and the deconvolver's real-frame readouts taking their star and structure masks from here | NOT STARTED |
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

## Traps

- `BitMatrix` indexes `[row, column]`; a segmentation map indexes `(x, y)`. `SegmentationMap.LabelAt`
  takes `(x, y)`; the masks are `BitMatrix` and take `[y, x]`.
- The mesh follows structure larger than about a cell. A nebula of 40 px sigma in 64 px cells survives
  only because of the second pass; a nebula that fills half the frame is the extractor's problem.
- `Image` planes must be hoisted once (`GetChannelSpan`), never read through the indexer per pixel;
  both classes take a `ReadOnlySpan<float>` plus width and height for that reason and offer an
  `Image` overload only as a convenience.
