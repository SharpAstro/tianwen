"""A diagnostic, not a product step (2026-11-20): R4f's target rule applied to a starless plate we already have.

    python -I grain_rule_post.py <input master fits> <our starless fits> <out fits>

The grain rule of training/star-removal/plate_grain.py (grain_out) run on OUR starless plate of a scoring master, so the
plate holds what a perfect learner of R4f's targets would write there: where the detail between 7 and 31 px is weak, the
smooth glow plus pixel noise; elsewhere the plate as it is. Scored like any plate it says, before R4f has a score, what
that target can read on the registered rows.

The rule wants the density of the stars the plate builder subtracted. A scoring master has no builder run, so the density
here is of the INPUT's compact peaks: local maxima over 5 px standing 6 LOCAL pixel sigma (plate_grain.noise_map) over
the input's 15 px median, per pixel in a 61 px box, on the channels' mean. Calibrated on five store sessions against
the builder's own subtracted stars (grain_rule_density_cal.py): between 5 and 8 sigma the count and the crowded share match
on the Tarantula, the SMC and the Rosette, and M33 and the Helix stay uncrowded as the builder has them.
Printed: the share of the frame the rule counts as crowded and smooths.
"""
import sys
from pathlib import Path

import numpy as np
from astropy.io import fits
from scipy.ndimage import maximum_filter, median_filter, uniform_filter

sys.path.insert(0, str(Path(__file__).resolve().parent))   # python -I leaves the script's folder off the path
import plate_grain  # noqa: E402

MASTER, OURS, OUT = sys.argv[1:4]
m = np.asarray(fits.getdata(MASTER), np.float32)
m3 = m[None] if m.ndim == 2 else m[:3]
with fits.open(OURS) as h:
    o = np.asarray(h[0].data, np.float32); header = h[0].header
mono = o.ndim == 2
o3 = o[None] if mono else o
lum = np.nanmean(m3, axis=0)
ok = np.isfinite(lum) & (lum != 0)
L = np.where(ok, lum, np.nanmedian(lum[ok]))
sig = plate_grain.noise_map(L)   # the LOCAL noise: a frame-wide figure counts a bright galaxy's own noise as stars
peaks = (L == maximum_filter(L, 5)) & (L - median_filter(L, 15) > 6 * np.maximum(sig, 1e-12)) & ok
dens = uniform_filter(peaks.astype(np.float32), 61)
w = np.clip((dens - 0.003) / 0.005, 0, 1)
print(f"{int(peaks.sum())} compact peaks in the input; density over the rule's 0.008 a pixel on {100 * (w >= 1).mean():.1f} % of the frame, over 0.003 on {100 * (w > 0).mean():.1f} %")
out, note = plate_grain.grain_out(o3, dens, "rule-post")
print(" | ".join(note) or "untouched")
changed = np.abs(out - o3).max(axis=0) > 0
print(f"pixels changed: {100 * changed.mean():.1f} %")
fits.PrimaryHDU(out[0] if mono else out, header).writeto(OUT, overwrite=True)
print(OUT)
