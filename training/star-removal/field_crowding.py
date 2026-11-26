"""How crowded a real master's stars stand, by the count --crowd-weight reads on a training draw, so the two can be set side
by side.

    python field_crowding.py <master.fits> <starless plate.fits> [--sigma 3] [--radius 16]

The star layer is the master less a remover's starless plate of it (the judge's, StarXTerminator's, for the scorecard's
masters), on the channels' mean, in the plate's own noise (the spread of horizontal neighbour differences over the frame);
its local maxima (3 x 3) at or above `sigma` are counted in the square of side 2 x radius + 1 about each pixel, as
n2n_smoke.py's crowd_weights counts a draw's. Prints the count by percentile over the frame (less a radius at each edge)
and the share of the frame at 20 and at 33 or more. training/denoise/crowd_weight_share.py prints the same count for a
training cache. Reads only. Needs torch (CPU is enough).
"""
import argparse

import numpy as np
import torch
from astropy.io import fits

p = argparse.ArgumentParser()
p.add_argument("master")
p.add_argument("plate")
p.add_argument("--sigma", type=float, default=3.0)
p.add_argument("--radius", type=int, default=16)
a = p.parse_args()


def lum(path):
    x = np.asarray(fits.getdata(path), np.float32)
    return x[:3].mean(axis=0) if x.ndim == 3 else x


master, plate = lum(a.master), lum(a.plate)
# A plate's canvas ring is NaN (the pipeline's): it is left out of the noise and holds no peak.
sigma = 1.4826 * np.nanmedian(np.abs(np.diff(plate, axis=1))) / np.sqrt(2)
z = torch.from_numpy(np.nan_to_num((master - plate) / sigma, nan=0.0))[None, None]
peaks = ((z >= a.sigma) & (z == torch.nn.functional.max_pool2d(z, 3, 1, 1))).float()
side = 2 * a.radius + 1
r = a.radius
count = (torch.nn.functional.avg_pool2d(peaks, side, 1, r, count_include_pad=True) * side * side)[0, 0].numpy()[r:-r, r:-r]
print(f"{a.master} less {a.plate}: peaks at {a.sigma:g} sigma or more in the {side} px square")
print("count by percentile over the frame: " + ", ".join(f"p{q} {np.percentile(count, q):.0f}" for q in (10, 50, 90, 99, 99.9)))
print(f"share of the frame at 20 or more: {100 * np.mean(count >= 20):.1f} %; at 33 or more: {100 * np.mean(count >= 33):.1f} %; "
      f"most {count.max():.0f}")
