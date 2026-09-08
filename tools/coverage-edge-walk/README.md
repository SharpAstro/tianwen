# coverage-edge-walk

The harness the constants in `CoverageEdgeWalk` were measured with, kept so they can be re-measured
rather than re-argued. It prints, for each edge of a stacked master, how the local noise falls off
going inward -- and, where the master has a coverage plane, what the coverage actually was at the same
depths, so the estimate can be scored against ground truth.

```bash
python tools/coverage-edge-walk/edge-noise-profile.py <master.fits> [more.fits ...]
```

Needs `astropy` and `numpy`. Writes `nw8-results.json` beside the script with the full profiles, and
prints the per-edge trims a slope rule would produce at several thresholds.

## What it measures

Per edge, at 4 px steps in from the largest zero-free rectangle: p10 of the per-tile sigma of adjacent
differences ALONG the band (tiles of 64 px, bands 16 px deep). Differencing along the band kills any
gradient across it -- across is the direction a coverage ramp runs -- and the tile percentile keeps a
star, a trail or a nebula edge from deciding the answer.

Where `<master>.rejection.fits` exists AND was written by a drizzle strategy, it holds the accumulated
per-pixel WEIGHT, which is coverage. The script reports coverage at the same depths, so a row reads
"noise 1.53x, coverage 0.94" and the estimate can be checked rather than believed.

## Why the shipped rule is what it is

Measured on four real masters (three Astro Pixel Processor composites, one TianWen 10P Bayer-drizzle
master with its weight map):

| 10P edge | coverage says | interior-relative rule | plateau rule | slope/knee rule |
|---|---|---|---|---|
| top | 56 px | 68-76 | 68-76 | 68-76 |
| bottom | 56 px | 48-52 | 48-52 | 48-56 |
| right | 20 px | 12-16 or the cap | 12-16 | 8-16 |
| **left** | **4 px** | **312-460** | **312** | **432-456** |

The left edge is the case that decides the design: its vertical-difference noise peaks at 2.05x the
centre 76 px in and decays over ~460 px, all at full coverage, because a canvas edge is fed by fewer
distinct dither phases and its noise is therefore less correlated. Every rule that matches a LEVEL
trims hundreds of px of it. What the shipped walk does instead is refuse: it trims only a band whose
END is visible inside its bound (5% of the span), and only when the outermost band is the noisiest
part of the profile.

The same master's right edge shows why the walk can never replace a coverage plane: at 69% coverage it
reads 1.08x, because partial coverage raises the sample noise and correlates the neighbours at the same
time, and on drizzled data the two nearly cancel.
