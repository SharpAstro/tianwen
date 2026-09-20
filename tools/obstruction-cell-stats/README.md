# obstruction-cell-stats

The measurement pass behind [`docs/plans/stacking-obstruction-detection.md`](../../docs/plans/stacking-obstruction-detection.md)
(backlog `#48`). Throwaway analysis scripts, in the pattern of `tools/coverage-edge-walk/`: they
answer what the thresholds should be, they are not the feature.

Run against a folder of raw subs:

```bash
python cell_background.py "<folder of lights>" [grid]      # which frames are obstructed, and where
python band_stars.py      "<folder of lights>" [grid]      # do the stars fall inside the band too
python band_profile.py    "<folder of lights>" [frame]     # how wide is the ramp, is there an edge
```

## What each one is for

- **`cell_background.py`** divides every frame into a grid and reports each cell's background
  against that frame's OWN cell median. A frame-wide change (the sky darkening, the moon, a
  transparency swing) cancels by construction, so what is left is spatial. Obstructed frames show as
  a run of cells well below 1.0; a clean frame's floor is its vignetting corner and sits a couple of
  percent down with no structure.
- **`band_stars.py`** asks whether the star count falls where the background does. It thresholds
  ONCE per frame, off the clear cells. Thresholding per cell is the trap: inside the band the median
  and the MAD are both lower, so a per-cell threshold follows the obstruction down and reports a
  flat star count no matter how many stars are gone.
- **`band_profile.py`** fits the band's centre line through the flagged cells and profiles the
  background against perpendicular distance, with a median per bin so the stars do not enter it.
  This is the number the remedy turns on: if the transition has an edge, an obstructed frame can be
  masked and kept; if it is a long smooth ramp, there is nowhere to cut and the frame is dropped.

## Two things that will bite

**Split by photosite before any median.** All three scripts take a single colour population out of
the mosaic (`a[0::2, 1::2]`) rather than working on the mosaic. Four colours at four levels put a
colour pattern into every cell statistic, and a non-neutral white balance scales it further. Same
rule `BadPixelDetection` and `ClassicalBackgroundExtractor` had to learn.

**The reference frame matters more than the statistic.** Every comparison here is a cell against
other cells of the SAME frame. Comparing against another frame, or against a session median, puts
transparency and sky brightness back into the answer, which is exactly what the whole-frame gate
already cannot see past.

## What it measured

A powerline over the Helix Nebula, `E:/Astro/SharpCap Captures/Helix Nebula RGB 120s -4deg 121g 11o`
(2022-08-31, ASI533MC Pro at 135 mm, 46 x 120 s). Six frames of 46; band 15% down on background and
0.5 to 0.75 on star count; a symmetric ramp reaching clear only past 210 px with no step in it. The
numbers and what they changed in the design are in the plan.
