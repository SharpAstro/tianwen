"""How wide is the transition at the edge of an obstruction, and is there an edge at all?

This is the number the remedy turns on. A body close enough to be far outside focus casts mostly
penumbra rather than shadow, with diffraction at the boundary underneath it, so the profile can be a
long smooth ramp with the geometric edge unmarked anywhere along it. There is then nothing to draw a
mask at, only a choice of how much good frame to burn, and dropping the sub is cheaper.

Method: fit the band's centre line through the cells the background rule flags (total least
squares), then bin every photosite by signed perpendicular distance and take a MEDIAN per bin, which
is what keeps the stars out of the profile. Distances are reported in FULL-FRAME pixels.

Usage: python band_profile.py "<folder of lights>" [frame=1] [grid=24] [bin=10] [span=320]
"""
import glob
import os
import sys
import warnings

import numpy as np

warnings.filterwarnings("ignore")
from astropy.io import fits  # noqa: E402


def main(root, frame, grid, binpx, span):
    files = sorted(glob.glob(os.path.join(root, "**", "*.fits"), recursive=True))
    if not files:
        print(f"no FITS under {root}")
        return 1
    path = files[frame - 1]
    with fits.open(path, memmap=False) as hdul:
        head = hdul[0].header
        plane = hdul[0].data[0::2, 1::2].astype(np.float32)
    h, w = plane.shape
    ch, cw = h // grid, w // grid
    bg = np.array([[np.median(plane[y * ch:(y + 1) * ch, x * cw:(x + 1) * cw])
                    for x in range(grid)] for y in range(grid)], np.float32)
    rel = bg / np.median(bg)
    ys, xs = np.nonzero(rel < 0.95)
    if len(ys) < 6:
        print(f"{os.path.basename(path)}: fewer than 6 cells below 0.95, nothing to profile")
        return 1
    px, py = (xs + 0.5) * cw, (ys + 0.5) * ch
    mx, my = px.mean(), py.mean()
    _, _, vt = np.linalg.svd(np.column_stack([px - mx, py - my]))
    along = vt[0]
    perp = np.array([-along[1], along[0]])
    yy, xx = np.mgrid[0:h, 0:w]
    dist = (((xx - mx) * perp[0] + (yy - my) * perp[1]) * 2.0).astype(np.float32)  # -> full-frame px
    level = float(np.median(bg[rel > 0.995]))
    scale = None
    if head.get("XPIXSZ") and head.get("FOCALLEN"):
        scale = 206.265 * float(head["XPIXSZ"]) / float(head["FOCALLEN"])
    print(f"{os.path.basename(path)}  band {np.degrees(np.arctan2(along[1], along[0])):+.1f} deg, "
          f"clear {level:.1f} ADU" + (f", {scale:.2f} arcsec/px" if scale else ""))
    print(f'{"perp px":>9s} {"arcmin":>8s} {"median":>9s} {"deficit":>9s}')
    prof = []
    for a in np.arange(-span, span + 1, binpx):
        m = (dist >= a) & (dist < a + binpx)
        if m.sum() < 3000:
            continue
        v = float(np.median(plane[m]))
        pct = 100.0 * (v - level) / level
        prof.append((a + binpx / 2, pct))
        arcmin = (a + binpx / 2) * scale / 60 if scale else float("nan")
        print(f"{a + binpx / 2:+9.0f} {arcmin:+8.2f} {v:9.1f} {pct:+8.2f}% "
              + ("#" * min(60, int(abs(pct) * 2)) if pct < 0 else "+"))
    core = min(prof, key=lambda p: p[1])
    print(f"\ncore {core[1]:+.2f}% at {core[0]:+.0f} px")
    for name, side in (("inward", -1), ("outward", 1)):
        rec = [p for p in prof if (p[0] - core[0]) * side > 0 and abs(p[1]) < 0.5]
        if rec:
            edge = min(rec, key=lambda p: abs(p[0] - core[0]))
            print(f"  recovers to within 0.5% {name} at {edge[0]:+.0f} px "
                  f"({abs(edge[0] - core[0]):.0f} px from the core)")
    print("\nwhat a cut would cost:")
    half = max(abs(p[0]) for p in prof if p[1] < -0.5)
    for margin in (0, 50, 100, 150, 200, 250):
        print(f"  shadow plus {margin:3d} px margin -> "
              f"{100 * float((np.abs(dist) <= half + margin).mean()):5.1f}% of the frame")
    return 0


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        raise SystemExit(2)
    args = sys.argv[2:] + ["1", "24", "10", "320"][len(sys.argv) - 2:]
    raise SystemExit(main(sys.argv[1], int(args[0]), int(args[1]), int(args[2]), int(args[3])))
