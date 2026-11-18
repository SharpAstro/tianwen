"""Parity of the C# dig-fill step (DigFill, `tianwen image fill-digs`) with whole_floor.py's floor3. Glue only.

    python -I dig_fill_parity.py <w-tag> <cli> <out-dir> [scorecard.json]

For each whole master (andromeda, tarantula, sag) it runs `<cli> image fill-digs` on the scorecard's input master and
f2-whole-master/<w-tag>/<name>_starless.fits, writing <out-dir>/<name>_starless.filled.fits, and compares that with
f2-whole-master/<w-tag>+floor3/<name>_starless.post.fits (whole_floor.py <w-tag> floor3). Nothing is written under the
scratch root's own folders. Per master and channel it prints, in units of the input's noise sigma (1.4826 MAD of the input
less its 5 px median, over the pixels present in both): the largest and the median absolute difference, the share of pixels
that differ by more than 0.01 sigma, and how many pixels are not bit for bit the same. It exits 1 when a pixel is absent in
one result and present in the other.
"""
import json, os, subprocess, sys
import numpy as np
from astropy.io import fits
from scipy.ndimage import median_filter

S = os.environ.get("TIANWEN_SR_SCRATCH", "C:/temp/tianwen-scratch")
tag, cli, out_dir = sys.argv[1], sys.argv[2], sys.argv[3]
cfg = json.load(open(sys.argv[4] if len(sys.argv) > 4 else f"{S}/whole-blend3-scorecard.json", encoding="utf-8"))
os.makedirs(out_dir, exist_ok=True)
bad = False
print("| master | channel | max abs diff (sigma) | median abs diff (sigma) | share over 0.01 sigma | pixels not bit-equal | pixels the step changed |")
print("|---|---|---|---|---|---|---|")
for name in ("andromeda", "tarantula", "sag"):
    master = cfg["masters"][name]; plate = f"{S}/f2-whole-master/{tag}/{name}_starless.fits"
    ours = f"{out_dir}/{name}_starless.filled.fits"
    subprocess.run([cli, "image", "fill-digs", "--input", master, "--starless", plate, "-o", ours], check=True, stdout=subprocess.DEVNULL)
    a = np.asarray(fits.getdata(ours), np.float32); b = np.asarray(fits.getdata(f"{S}/f2-whole-master/{tag}+floor3/{name}_starless.post.fits"), np.float32)
    x = np.asarray(fits.getdata(master), np.float32); o = np.asarray(fits.getdata(plate), np.float32)
    if a.ndim == 2: a, b, x, o = a[None], b[None], x[None], o[None]
    assert a.shape == b.shape == x.shape, (name, a.shape, b.shape, x.shape)
    for c in range(a.shape[0]):
        if (np.isnan(a[c]) != np.isnan(b[c])).any():
            bad = True; print(f"{name} channel {c}: {(np.isnan(a[c]) != np.isnan(b[c])).sum()} pixels absent in one result only")
        ok = ~np.isnan(a[c]) & ~np.isnan(b[c])
        h = (x[c] - median_filter(x[c], 5))[ok]; sigma = 1.4826 * np.median(np.abs(h - np.median(h)))
        d = np.abs(a[c].astype(np.float64) - b[c])[ok] / sigma
        changed = (b[c] != o[c])[ok].sum()
        print(f"| {name} | {c} | {d.max():.3g} | {np.median(d):.3g} | {100 * (d > 0.01).mean():.4f} % | {(a[c] != b[c])[ok].sum()} of {ok.sum()} | {changed} |")
sys.exit(1 if bad else 0)
