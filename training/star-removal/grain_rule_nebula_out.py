"""A diagnostic (2026-11-20): the grain-rule plate with the NEBULA held out of the rule.
    python -I grain_rule_nebula_out.py <master> <base starless> <rule plate> <out fits> [nebula sigma, default 20] [feather px, default 8]
out = base where the nebula is, the rule plate elsewhere, blended over a Gaussian feather. The nebula is nebula_rows.py's
own mask: the input's large-scale map (8 px block medians through a 7-block median) standing <nebula sigma> pixel sigmas
over the sky (the map's 5th percentile). Prints the mask's share of the frame."""
import sys
import numpy as np
from astropy.io import fits
from scipy.ndimage import gaussian_filter, median_filter

MASTER, BASE, RULE, OUT = sys.argv[1:5]
NS = float(sys.argv[5]) if len(sys.argv) > 5 else 20.0
FEATHER = float(sys.argv[6]) if len(sys.argv) > 6 else 8.0
BLOCK, FILTER = 8, 7
m = np.asarray(fits.getdata(MASTER), np.float32)
inp = np.nan_to_num(m if m.ndim == 2 else m[:3].mean(axis=0)); h, w = inp.shape
sig = 1.4826 * float(np.median(np.abs(np.diff(inp[::4], axis=1)))) / np.sqrt(2)
bh, bw = h // BLOCK, w // BLOCK
blocks = np.median(inp[:bh * BLOCK, :bw * BLOCK].reshape(bh, BLOCK, bw, BLOCK).transpose(0, 2, 1, 3).reshape(bh, bw, -1), axis=2)
small = median_filter(blocks, FILTER)
big = np.zeros_like(inp); big[:bh * BLOCK, :bw * BLOCK] = np.repeat(np.repeat(small, BLOCK, 0), BLOCK, 1)
neb = big - float(np.percentile(small, 5)) > NS * sig
wt = np.clip(gaussian_filter(neb.astype(np.float32), FEATHER) * 2, 0, 1) if FEATHER > 0 else neb.astype(np.float32)
wt = np.maximum(wt, neb)   # the whole mask is held out; the feather only widens it
with fits.open(BASE) as f:
    base = np.asarray(f[0].data, np.float32); header = f[0].header
rule = np.asarray(fits.getdata(RULE), np.float32)
out = wt * base + (1 - wt) * rule if base.ndim == 2 else wt[None] * base + (1 - wt[None]) * rule
out = np.where(np.isfinite(base), out, base)
print(f"nebula over {NS:g} sigma: {100 * neb.mean():.1f} % of the frame; held out with its feather: {100 * (wt > 0).mean():.1f} %")
fits.PrimaryHDU(out.astype(np.float32), header).writeto(OUT, overwrite=True)
print(OUT)
