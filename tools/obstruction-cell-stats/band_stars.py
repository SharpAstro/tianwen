"""Does the star count fall where the background does?

The threshold is set ONCE per frame, off the cells the background rule calls clear, and then applied
everywhere. Thresholding each cell against its own median and MAD is the trap this exists to avoid:
inside an obstructed band both are lower, so the threshold follows the obstruction down and the
count cannot drop however many stars are gone. That version of this script reported a perfectly flat
star count across a band that is demonstrably 15% down.

The count is a PROXY for a star count, deliberately crude and used only to answer "does it move".
A production detector reads the star list the registration pass already has.

Usage: python band_stars.py "<folder of lights>" [grid=24]
"""
import glob
import os
import sys
import warnings

import numpy as np

warnings.filterwarnings("ignore")
from astropy.io import fits  # noqa: E402


def main(root, grid):
    files = sorted(glob.glob(os.path.join(root, "**", "*.fits"), recursive=True))
    if not files:
        print(f"no FITS under {root}")
        return 1
    print(f'{"frame":6s} {"time":9s} {"band cells":>10s} {"bg band":>8s} {"bg clear":>9s} | '
          f'{"stars band":>10s} {"stars clear":>11s} {"ratio":>6s}')
    for i, path in enumerate(files):
        with fits.open(path, memmap=False) as hdul:
            plane = hdul[0].data[0::2, 1::2].astype(np.float32)
            when = str(hdul[0].header.get("DATE-OBS", ""))[11:19]
        h, w = plane.shape
        ch, cw = h // grid, w // grid
        bg = np.array([[np.median(plane[y * ch:(y + 1) * ch, x * cw:(x + 1) * cw])
                        for x in range(grid)] for y in range(grid)], np.float32)
        rel = bg / np.median(bg)
        band, clear = rel < 0.95, rel > 0.995
        if not band.any():
            continue
        # ONE threshold for the whole frame, anchored on the clear part
        level = float(np.median(bg[clear])) if clear.any() else float(np.median(bg))
        noise = float(np.median(np.abs(plane - np.median(plane))) * 1.4826)
        thr = level + 8 * noise
        cnt = np.array([[float((plane[y * ch:(y + 1) * ch, x * cw:(x + 1) * cw] > thr).sum())
                         for x in range(grid)] for y in range(grid)], np.float32)
        sb, sc = float(cnt[band].mean()), float(cnt[clear].mean()) if clear.any() else float("nan")
        print(f"{i + 1:6d} {when:9s} {int(band.sum()):10d} {np.median(rel[band]):8.4f} "
              f"{np.median(rel[clear]):9.4f} | {sb:10.1f} {sc:11.1f} {sb / sc if sc else float('nan'):6.2f}")
    return 0


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        raise SystemExit(2)
    raise SystemExit(main(sys.argv[1], int(sys.argv[2]) if len(sys.argv) > 2 else 24))
