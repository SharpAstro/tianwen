"""Which frames of a session carry a spatial background deficit, and where in the frame it sits.

Every cell is compared against its OWN frame's cell median, so a frame-wide change cancels: the sky
darkening over a night is roughly ten times the depth of the obstruction this was written for, and
it does not appear here at all.

The statistic is taken over ONE photosite population of the mosaic, never the mosaic as a whole.

Usage: python cell_background.py "<folder of lights>" [grid=24]
"""
import glob
import json
import os
import sys
import warnings

import numpy as np

warnings.filterwarnings("ignore")
from astropy.io import fits  # noqa: E402


def cell_medians(plane, grid):
    h, w = plane.shape
    ch, cw = h // grid, w // grid
    return np.array([[np.median(plane[y * ch:(y + 1) * ch, x * cw:(x + 1) * cw])
                      for x in range(grid)] for y in range(grid)], np.float32)


def main(root, grid):
    files = sorted(glob.glob(os.path.join(root, "**", "*.fits"), recursive=True))
    if not files:
        print(f"no FITS under {root}")
        return 1
    print(f"{len(files)} frames, {grid}x{grid} grid\n")
    out = []
    for i, path in enumerate(files):
        with fits.open(path, memmap=False) as hdul:
            data = hdul[0].data
            when = str(hdul[0].header.get("DATE-OBS", ""))
        plane = data[0::2, 1::2].astype(np.float32)   # one colour of the mosaic
        cells = cell_medians(plane, grid)
        med = float(np.median(cells))
        rel = cells / med
        low = int((rel < 0.95).sum())
        out.append(dict(i=i + 1, name=os.path.basename(path), when=when, median=med,
                        minrel=float(rel.min()), cells_below_095=low,
                        rel=[round(float(v), 4) for v in rel.ravel()]))
        print(f"{i + 1:4d} {os.path.basename(path)[-22:]:22s} {when[11:19]:9s} med={med:8.1f} "
              f"min={rel.min():.4f} max={rel.max():.4f} cells<0.95={low:4d}", flush=True)
    with open("obstruction_cells.json", "w", encoding="utf-8") as f:
        json.dump(out, f)
    flagged = [r for r in out if r["cells_below_095"] > 0]
    print(f"\n{len(flagged)} of {len(out)} frames carry a cell below 0.95 of their own cell median")
    if flagged:
        print("  frames: " + ", ".join(str(r["i"]) for r in flagged[:40]))
    # The spatial test is deliberately blind to a frame-wide level change, so say what it did not
    # look at: a session that brightens into dawn, or a frame at full well, is a DIFFERENT defect
    # and needs the frame's own median against the session, not a cell against its neighbours.
    meds = sorted(r["median"] for r in out)
    lo, hi = meds[0], meds[-1]
    if hi > 4 * max(lo, 1):
        print(f"  NOTE frame medians span {lo:.0f} to {hi:.0f} ADU ({hi / max(lo, 1):.0f}x): this "
              f"session has a frame-wide level problem that the per-cell test does not see")
    print("wrote obstruction_cells.json")
    return 0


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        raise SystemExit(2)
    raise SystemExit(main(sys.argv[1], int(sys.argv[2]) if len(sys.argv) > 2 else 24))
