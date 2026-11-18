"""Is the remover invariant to a per-channel gain (a colour balance, a linear fit)? (2026-11-18)

The master is multiplied per channel by a gain (never over 1, so nothing clips), the remover is run, the output is
divided by the same gain and compared with the output of the untouched master. Per catalogued star, by significance
band: the difference in rings round the star, in the star's own catalogued noise sigma, per channel; and against the
plate (the truth) both ways, so a change in the dig shows.

    python -I wb_invariance.py <onnx> <session stem> [<session stem> ...]
"""
import csv, os, subprocess, sys
import numpy as np
from astropy.io import fits

STORE = os.environ.get("TIANWEN_SR_STORE", "D:/Astro-Dataset/2026-10-12-full")
OUT = os.environ.get("TIANWEN_SR_OUT", "D:/temp/tianwen-scratch/wb-invariance")
CLI = os.environ.get("TIANWEN_SR_CLI", "tianwen")
GAINS = [("r-half", (0.5, 1.0, 1.0)), ("g-half", (1.0, 0.5, 1.0)), ("b-half", (1.0, 1.0, 0.5)), ("r-quarter", (0.25, 1.0, 1.0))]
RINGS = [(0, 2), (2, 4), (4, 8)]
BANDS = [("20-100", 20, 100), ("100-1000", 100, 1000), (">=1000", 1000, 1e9)]
onnx, stems = sys.argv[1], sys.argv[2:]
os.makedirs(OUT, exist_ok=True)


def remove(src, dst):
    if not os.path.exists(dst):
        r = subprocess.run([CLI, "image", "remove-stars", src, "-o", dst, "--no-stars", "--views", "1", "--model", onnx],
                           capture_output=True, text=True, creationflags=subprocess.CREATE_NO_WINDOW)
        if r.returncode != 0:
            raise SystemExit(f"remove-stars failed on {src}:\n{r.stdout[-2000:]}\n{r.stderr[-2000:]}")
    return np.asarray(fits.getdata(dst), dtype=np.float32)


yy, xx = np.mgrid[-8:9, -8:9]
rr = np.hypot(yy, xx)
masks = [(rr >= a) & (rr < b) for a, b in RINGS]
for stem in stems:
    tag = stem.split("_")[0] + "-" + str(sum(map(ord, stem)) % 1000)
    with fits.open(f"{STORE}/session-masters/{stem}.fits") as h:
        master = np.asarray(h[0].data, dtype=np.float32); header = h[0].header
    plate = np.asarray(fits.getdata(f"{STORE}/starless/plates/{stem}_plate.fits"), dtype=np.float32)
    base = remove(f"{STORE}/session-masters/{stem}.fits", f"{OUT}/{tag}-base-out.fits")
    stars = [r for r in csv.DictReader(open(f"{STORE}/starless/plates/{stem}_plate.stars.csv", encoding="utf-8"))]
    H, W = master.shape[1:]
    print(f"\n{stem}\n  sky medians {[round(float(np.nanmedian(c[::8, ::8])), 5) for c in master]}, {len(stars)} catalogued")
    for gname, gain in GAINS:
        g = np.array(gain, dtype=np.float32)[:, None, None]
        src = f"{OUT}/{tag}-{gname}.fits"
        if not os.path.exists(src):
            fits.PrimaryHDU(master * g, header).writeto(src)
        out = remove(src, f"{OUT}/{tag}-{gname}-out.fits") / g
        diff = out - base
        print(f"  gain {gain}: whole frame, median |scaled back - base| per channel "
              f"{[float(np.nanmedian(np.abs(c[::4, ::4]))) for c in diff]}")
        print(f"    {'band':10s} {'ch':2s} {'n':>6s}" + "".join(f"   r{a}-{b}: med diff  |diff|>1 %   dug% base  dug% gain" for a, b in RINGS))
        for bname, lo, hi in BANDS:
            for ch in range(3):
                rows = [[] for _ in RINGS]; eb = [[] for _ in RINGS]; eg = [[] for _ in RINGS]
                for s in stars:
                    sg = float(s["significance"])
                    if not (lo <= sg < hi) or s["saturated"] == "1" or s["second_pass"] != "0":
                        continue
                    x, y = int(round(float(s["x"]))), int(round(float(s["y"])))
                    if not (80 <= x < W - 80 and 80 <= y < H - 80):
                        continue
                    sl = (ch, slice(y - 8, y + 9), slice(x - 8, x + 9))
                    d, b, o, p = diff[sl], base[sl], out[sl], plate[sl]
                    if not (np.isfinite(d).all() and np.isfinite(p).all()):
                        continue
                    sig = float(s["sigma"])
                    for i, m in enumerate(masks):
                        rows[i].append(d[m].mean() / sig); eb[i].append((b - p)[m].mean() / sig); eg[i].append((o - p)[m].mean() / sig)
                n = len(rows[0])
                if not n:
                    continue
                line = f"    {bname:10s} {'RGB'[ch]:2s} {n:6d}"
                for v, vb, vg in zip(rows, eb, eg):
                    v, vb, vg = np.array(v), np.array(vb), np.array(vg)
                    line += f"   {np.median(v):16.3f} {100 * (np.abs(v) > 1).mean():10.1f} {100 * (vb < -1).mean():10.1f} {100 * (vg < -1).mean():10.1f}"
                print(line, flush=True)
