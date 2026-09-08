"""Knee rule: walk in while the noise profile is still FALLING, stop where it flattens.

Level matching against any reference -- the frame interior or the edge's own deep plateau --
was refuted by the drizzle master's left edge: its vertical-difference noise decays over
several HUNDRED px while the weight map says coverage is complete 12 px in, so every margin
from 1.05 to 1.30 trimmed 312 px where the truth was 4. The decay is a correlation
gradient, not missing exposure.

A slope test does not care about the level, only whether the profile is still coming down:
trim = the deepest depth d where p(d) / p(d + LOOK) > 1 + SLOPE.
"""
import os, sys, json
import numpy as np
from astropy.io import fits

K = 16
TILE = 64
STEP = 4
PCT = 10
CAPFRAC = 0.15
LOOKS = (32, 64)
SLOPES = (0.03, 0.05, 0.08, 0.12)
SMOOTH = 3


def load(path):
    with fits.open(path, memmap=False) as hl:
        for h in hl:
            if h.data is not None and h.data.ndim >= 2:
                d = np.asarray(h.data, dtype=np.float32)
                break
    return d[None] if d.ndim == 2 else d


def absent_mask(d):
    return np.isnan(d).any(axis=0) | (np.nan_to_num(d, nan=1.0) == 0).all(axis=0)


def zero_free_rect(absent):
    h, w = absent.shape
    f = 4
    ch, cw = h // f, w // f
    coarse = absent[:ch * f, :cw * f].reshape(ch, f, cw, f).any(axis=(1, 3))
    heights = np.zeros(cw, dtype=np.int32)
    best, bestarea = (0, 0, 0, 0), 0
    for y in range(ch):
        heights = np.where(coarse[y], 0, heights + 1)
        st, hh = [], heights
        for x in range(cw + 1):
            cur = hh[x] if x < cw else 0
            while st and hh[st[-1]] >= cur:
                bh = hh[st.pop()]
                left = st[-1] + 1 if st else 0
                bw = x - left
                if bw * bh > bestarea:
                    bestarea, best = bw * bh, (left, y - bh + 1, bw, bh)
            if x < cw:
                st.append(x)
    x0, y0 = best[0] * f, best[1] * f
    x1, y1 = x0 + best[2] * f, y0 + best[3] * f
    for _ in range(8192):
        if x1 - x0 < 8 or y1 - y0 < 8: break
        if absent[y0, x0:x1].any(): y0 += 1; continue
        if absent[y1 - 1, x0:x1].any(): y1 -= 1; continue
        if absent[y0:y1, x0].any(): x0 += 1; continue
        if absent[y0:y1, x1 - 1].any(): x1 -= 1; continue
        break
    return x0, y0, x1, y1


def band_slice(rect, edge, depth):
    x0, y0, x1, y1 = rect
    if edge == 'top':
        return (slice(None), slice(y0 + depth, y0 + depth + K), slice(x0, x1))
    if edge == 'bottom':
        return (slice(None), slice(y1 - depth - K, y1 - depth), slice(x0, x1))
    if edge == 'left':
        return (slice(None), slice(y0, y1), slice(x0 + depth, x0 + depth + K))
    return (slice(None), slice(y0, y1), slice(x1 - depth - K, x1 - depth))


def tile_sigmas(band, horizontal):
    b = band if horizontal else band.T
    k, L = b.shape
    n = L // TILE
    if n < 1:
        return np.zeros(0)
    t = b[:, :n * TILE].reshape(k, n, TILE)
    dif = np.diff(t, axis=2)
    dif = np.moveaxis(dif, 1, 0).reshape(n, -1)
    med = np.median(dif, axis=1, keepdims=True)
    return 1.4826 * np.median(np.abs(dif - med), axis=1) / np.sqrt(2.0)


def smooth(a, n):
    if n <= 1:
        return a
    out = np.empty_like(a)
    for i in range(len(a)):
        lo, hi = max(0, i - n // 2), min(len(a), i + n // 2 + 1)
        out[i] = np.median(a[lo:hi])
    return out


def trim_by_slope(prof, depths, look, slope):
    step = depths[1] - depths[0]
    off = look // step
    worst = -1
    for i in range(len(prof) - off):
        if prof[i] / prof[i + off] > 1.0 + slope:
            worst = depths[i]
    return 0 if worst < 0 else worst + step


def run(path):
    d = load(path)
    c, h, w = d.shape
    absent = absent_mask(d)
    rect = zero_free_rect(absent)
    x0, y0, x1, y1 = rect
    cov = None
    rp = path.replace('.fits', '.rejection.fits')
    if 'rejection' not in path and os.path.exists(rp):
        cov = load(rp)
    print(f"\n=== {os.path.basename(path)} {w}x{h}x{c}  rect {x1-x0}x{y1-y0} at ({x0},{y0}) ===")
    res = []
    for edge in ('top', 'bottom', 'left', 'right'):
        horizontal = edge in ('top', 'bottom')
        span = (y1 - y0) if horizontal else (x1 - x0)
        cap = int(CAPFRAC * span)
        depths = list(range(0, cap, STEP))
        vals, covp = [], []
        for dp in depths:
            sl = band_slice(rect, edge, dp)
            vals.append(max(np.percentile(tile_sigmas(d[sl][k], horizontal), PCT) for k in range(c)))
            if cov is not None:
                covp.append(min(np.median(cov[sl][k]) for k in range(c)))
        prof = smooth(np.asarray(vals, dtype=np.float64), SMOOTH)
        line = f"-- {edge:6s} cap {cap:3d}"
        trims = {}
        for look in LOOKS:
            cells = []
            for s in SLOPES:
                t = trim_by_slope(prof, depths, look, s)
                trims[(look, s)] = t
                cells.append(f"{s:.2f}->{t:3d}")
            line += f" | look{look}: " + " ".join(cells)
        if cov is not None:
            cp = np.asarray(covp, dtype=np.float64)
            full = np.median(cp[len(cp) // 2:])
            covtrims = {}
            for t in (0.90, 0.95, 0.99):
                worst = -1
                for i, dp in enumerate(depths):
                    if cp[i] / full < t:
                        worst = dp
                covtrims[t] = 0 if worst < 0 else worst + STEP
            line += "  || cov: " + " ".join(f"{t:.2f}->{covtrims[t]:3d}" for t in (0.90, 0.95, 0.99))
        else:
            covtrims = None
        print(line)
        res.append(dict(edge=edge, cap=int(cap), depths=[int(x) for x in depths],
                        profile=[round(float(v), 8) for v in prof],
                        trims={f"{k[0]}/{k[1]}": int(v) for k, v in trims.items()},
                        cov={str(k): int(v) for k, v in covtrims.items()} if covtrims else None))
    return dict(file=os.path.basename(path), edges=res)


allres = [run(p) for p in sys.argv[1:]]
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "nw8-results.json")
json.dump(allres, open(out, "w"), indent=1)
print("\nwrote", out)
