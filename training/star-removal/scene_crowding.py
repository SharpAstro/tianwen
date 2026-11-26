"""The crowding count of a rendered synthetic-cluster scene, as field_crowding.py counts a master and crowd_weight_share.py
a training draw: the composite less its noisy starless truth (the stars and their own noise), in the noisy truth's own
noise, peaks at 3 sigma or more in the 33 px square about each pixel.

    python -I scene_crowding.py <scene base without .fits> ...
"""
import sys

import numpy as np
import torch
from astropy.io import fits


def lum(path):
    x = np.asarray(fits.getdata(path), np.float32)
    return x[:3].mean(axis=0) if x.ndim == 3 else x


for base in sys.argv[1:]:
    comp, truth = lum(f"{base}.fits"), lum(f"{base}_starless_noisy.fits")
    sigma = 1.4826 * np.median(np.abs(np.diff(truth, axis=1))) / np.sqrt(2)
    z = torch.from_numpy((comp - truth) / sigma)[None, None]
    peaks = ((z >= 3) & (z == torch.nn.functional.max_pool2d(z, 3, 1, 1))).float()
    count = (torch.nn.functional.avg_pool2d(peaks, 33, 1, 16, count_include_pad=True) * 33 * 33)[0, 0].numpy()[16:-16, 16:-16]
    print(f"{base}: " + ", ".join(f"p{q} {np.percentile(count, q):.0f}" for q in (10, 50, 90, 99)) +
          f"; at 20 or more {100 * np.mean(count >= 20):.1f} %")
