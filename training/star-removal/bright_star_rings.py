"""The light a remover leaves round each master's brightest stars, against a judge (#2292, 2026-11-20).

    python -I bright_star_rings.py --config <scorecard json> --judge w-sxt [--stars 12] <folder tag> [...]

On each master of the scorecard: the brightest compact peaks at least 420 px inside the frame and 150 px apart that the
JUDGE treats as stars (it leaves under a tenth of the peak's height). Round each, in rings out to 230 px, the ring's
median less the image's own sky (its median at 300 to 400 px), on the channels' mean, in the master's pixel sigma:

    taken by the judge  = input - judge
    left by a remover   = (remover - judge) / (input - judge)      0 = as the judge, 1 = the input untouched

A ring counts where the judge took at least one pixel sigma. Per folder: the median over the stars of what is left, per
ring, with how many stars the ring counted, for each master and over all. A folder's <name>_starless.post.fits is read
when it is there (a post step's plate), else <name>_starless.fits."""
import json, os, sys
import numpy as np
from astropy.io import fits
from scipy.ndimage import maximum_filter, uniform_filter

args = sys.argv[1:]
cfg = json.load(open(args[args.index("--config") + 1]))
judge = args[args.index("--judge") + 1]
N = int(args[args.index("--stars") + 1]) if "--stars" in args else 12
skip = {args.index(k) + 1 for k in ("--config", "--judge", "--stars") if k in args}
tags = [a for i, a in enumerate(args) if not a.startswith("--") and i not in skip]
W = cfg["out_root"]
EDGES = [8, 22, 40, 80, 120, 170, 230]


def lum(path):
    a = np.asarray(fits.getdata(path), np.float64)
    a = a[None] if a.ndim == 2 else a[:3]
    return np.nanmean(a, axis=0)


def plate(tag, name):
    post = os.path.join(W, tag, f"{name}_starless.post.fits")
    return lum(post if os.path.exists(post) else os.path.join(W, tag, f"{name}_starless.fits"))


def rings(img, d, sky_ring):
    sky = float(np.nanmedian(img[sky_ring]))
    return np.array([float(np.nanmedian(img[(d >= lo) & (d < hi)])) - sky for lo, hi in zip(EDGES[:-1], EDGES[1:])]), sky


left = {t: {} for t in tags}
took = {}
scored = []   # the masters that had at least one qualifying star
for name, path in cfg["masters"].items():
    I = lum(path); J = plate(judge, name)
    H, Wd = I.shape
    s = uniform_filter(np.nan_to_num(I, nan=0.0), 3)
    peak = (s == maximum_filter(s, 151))
    ys, xs = np.nonzero(peak)
    order = np.argsort(-s[ys, xs])
    stars = []
    picked = []
    for k in order:
        y, x = int(ys[k]), int(xs[k])
        if y < 420 or x < 420 or y >= H - 420 or x >= Wd - 420:
            continue
        if any(max(abs(y - sy), abs(x - sx)) < 150 for sy, sx in picked):
            continue          # maximum_filter(151) only keeps +-75 px, so the 150 px separation is enforced here
        y0, x0 = y - 400, x - 400
        cut = np.s_[y0:y + 401, x0:x + 401]
        yy, xx = np.mgrid[-400:401, -400:401]
        d = np.hypot(yy, xx); sky_ring = (d >= 300) & (d < 400)
        i_c, j_c = I[cut], J[cut]
        sig = 1.4826 * float(np.nanmedian(np.abs(np.diff(np.where(sky_ring, i_c, np.nan), axis=1)))) / np.sqrt(2)
        i_r, i_sky = rings(i_c, d, sky_ring); j_r, j_sky = rings(j_c, d, sky_ring)
        core = d < 3
        hi_i = float(np.nanmedian(i_c[core])) - i_sky; hi_j = float(np.nanmedian(j_c[core])) - j_sky
        if hi_i <= 0 or hi_j > 0.1 * hi_i:
            continue          # the judge keeps it: a nucleus or a knot, not a star
        stars.append((cut, d, sky_ring, sig, i_r, j_r, hi_i / sig))
        picked.append((y, x))
        if len(stars) == N:
            break
    if not stars:
        print(f"{name}: no star qualifies; skipped")
        continue
    scored.append(name)
    t =np.array([(st[4] - st[5]) / st[3] for st in stars])
    took[name] = t
    print(f"{name}: {len(stars)} stars, peak over sky {min(st[6] for st in stars):.0f} to {max(st[6] for st in stars):.0f} sigma; the judge took, median per ring (sigma): "
          + " / ".join(f"{v:.1f}" for v in np.median(t, axis=0)))
    for tag in tags:
        M = plate(tag, name)
        rows = []
        for (cut, d, sky_ring, sig, i_r, j_r, _), tk in zip(stars, t):
            m_r, _ = rings(M[cut], d, sky_ring)
            rows.append(np.where(tk >= 1.0, (m_r - j_r) / sig / np.maximum(tk, 1e-9), np.nan))
        left[tag][name] = np.array(rows)
print()
head = "".join(f"{lo}-{hi} px".rjust(14) for lo, hi in zip(EDGES[:-1], EDGES[1:]))
print(f"left by the remover, median over the stars (0 = as the judge, 1 = untouched); in brackets the stars a ring counted\n{'':44s}{head}")
for tag in tags:
    for name in scored + ["all"]:
        a = np.concatenate(list(left[tag].values())) if name == "all" else left[tag][name]
        line = "".join((f"{np.nanmedian(a[:, k]):.2f} [{int(np.isfinite(a[:, k]).sum())}]").rjust(14) if np.isfinite(a[:, k]).any() else "".rjust(14) for k in range(a.shape[1]))
        print(f"{tag[2:][:30]:30s} {name:12s} {line}")
