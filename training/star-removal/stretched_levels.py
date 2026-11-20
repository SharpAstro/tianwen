"""Does the level a halo has in the remover's own input say whether the model takes it? (#2292, 2026-11-20)

    python -I stretched_levels.py <store> <bright_star_list.tsv> <bright-stars-<model>.txt> [ours, default "r4d as trained"]

The remover feeds its models one whole-frame stretch per channel (ChunkedNafnetRunner.ApplyInputStretch: the channel's
minimum subtracted, then the midtones balance that puts the median above that minimum at 0.25). Per star and ring this
prints the ring's red median in that stretched domain beside the share of StarXTerminator's take that ours takes there
(from the ring file), and at the end the pairs in the order of the stretched level."""
import re, sys
import numpy as np
from astropy.io import fits

STORE, LIST, RINGFILE = sys.argv[1:4]
OURS = sys.argv[4] if len(sys.argv) > 4 else "r4d as trained"
EDGES = [(8, 22), (22, 40), (40, 60), (60, 80), (80, 100), (100, 120)]


def mtf(m, x):
    return (m - 1) * x / ((2 * m - 1) * x - m)


shares = {}
for block in open(RINGFILE, encoding="utf-8").read().split("=== ")[1:]:
    lines = block.splitlines()
    star = re.match(r"^\S+ \((.+?);", lines[0]).group(1)
    im, cur = {}, None
    for l in lines[1:]:
        m = re.match(r"^([^:]+): ring medians less", l)
        if m:
            cur = m.group(1); im[cur] = {}
            continue
        m = re.match(r"^\s*(\d+) to\s+(\d+)\s+(.*)$", l)
        if m and cur:
            im[cur][(int(m.group(1)), int(m.group(2)))] = [float(v) for v in m.group(3).split("|")[0].split("/")]
    shares[star] = {e: (im["master"][e][0], im["sxt"][e][0], im[OURS][e][0]) for e in EDGES}

params = {}
pairs = []
print(f"{'star':24s} {'median over min, R/G/B, in noise sigma':40s}  per ring: stretched red level of the master's ring : share of sxt's take that ours takes")
for line in open(LIST, encoding="utf-8"):
    p = line.rstrip("\n").split("\t")
    if p[0] in ("", "star") or p[0].startswith("#"):
        continue
    star, master, x, y = p[0], p[1], int(p[2]), int(p[3])
    with fits.open(f"{STORE}/session-masters/{master}.fits", memmap=True) as h:
        d = h[0].data
        if master not in params:
            full = np.asarray(d, np.float32)
            ok = np.isfinite(full) & (full != 0)
            top = float(np.nanmax(full))
            div = max(top, 65535.0) if top > 1.5 else max(top, 1.0)
            H, W = full.shape[-2:]
            out = []
            for c in range(3):
                v = full[c][ok[c]] / div
                mn = float(v.min()); med = float(np.median(v)) - mn
                sig = 1.4826 * float(np.median(np.abs(np.diff(full[c, H // 2 - 256:H // 2 + 256, W // 2 - 256:W // 2 + 256], axis=1)))) / np.sqrt(2) / div
                out.append((mn, med, 3 * med / (2 * med + 1), sig))
            params[master] = (div, out)
            del full
        div, per = params[master]
        R = 130
        cut = np.asarray(d[0, max(y - R, 0):y + R + 1, max(x - R, 0):x + R + 1], np.float32) / div - per[0][0]
        yy, xx = np.mgrid[max(y - R, 0) - y:max(y - R, 0) - y + cut.shape[0], max(x - R, 0) - x:max(x - R, 0) - x + cut.shape[1]]
        r = np.hypot(xx, yy)
    cells = []
    for e in EDGES:
        ring = cut[(r >= e[0]) & (r < e[1])]
        level = float(mtf(per[0][2], float(np.nanmedian(ring))))
        if e not in shares.get(star, {}):          # a row the ring file lacks (the uncertain south of Merope stars)
            cells.append('   n/a')
            continue
        mst, sxt, ours = shares[star][e]
        took = mst - sxt
        share = None if took < 3 else 100 * (mst - ours) / took
        cells.append(f"{level:.3f}:{'  -' if share is None else f'{share:3.0f}'}")
        if share is not None:
            pairs.append((level, share, star, e))
    depth = " / ".join(f"{pc[1] / pc[3]:5.1f}" for pc in per)
    print(f"{star:24s} {depth:40s}  " + "   ".join(cells))
print()
print("every star and ring where StarXTerminator takes 3 sigma or more, by the ring's stretched red level:")
pairs.sort()
for lo, hi in ((0.25, 0.30), (0.30, 0.35), (0.35, 0.40), (0.40, 0.50), (0.50, 0.60), (0.60, 0.70), (0.70, 0.85), (0.85, 1.01)):
    sel = [s for l, s, _, _ in pairs if lo <= l < hi]
    if sel:
        print(f"  level {lo:.2f} to {hi:.2f}: {len(sel):3d} rings, share taken median {np.median(sel):4.0f} %, 10th to 90th percentile {np.percentile(sel, 10):4.0f} to {np.percentile(sel, 90):4.0f}")
