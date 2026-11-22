"""What a refilled core looks like on the rebuilt plate, against the stored plate at the same stars (2026-11-23, #2449).

    python -I plate_refill_cores.py <master.fits> <stored plate.fits> <stored stars.csv> <rebuilt plate.fits>
                                    <rebuilt stars.csv> <fwhm px> [<out.png>]

Core level: the mean of the luminance within 2 px of the star, less the median of an annulus 6 to 10 px out. It is read
against its own null per plate: the same reading at 6,000 random spots with no listed source within 6 px. A core is
"bright" above the null's 99.5th percentile and "low" under its 0.5th, so 0.5 % each is what noise gives.

Groups: refilled (subtracted on both plates, not inpainted on the stored one, inpainted on the rebuilt one), untouched
(not inpainted on either), inpainted on both.

Sources: a plain finder run the same way on both plates (the luminance smoothed by the PSF, less the same smoothed six
times wider; a local maximum over 5 of the filtered plate's own robust sigma). A source of the rebuilt plate with none
within 1.5 px on the stored plate is new; it is placed by what it sits on, and the stored plate's filtered value at
that pixel says whether it was already nearly one (4 sigma or more) or not there. `<fwhm px>` is the luminance's
star width, the LAST element of `FwhmPx` in the store's report line (the builder's `lumPsf.Fwhm`), not the first
channel's; the plan's figures are at that width (2.111 px on Centaurus A). The core levels do not depend on it.

With an output path it also writes look crops (master, stored plate, rebuilt plate, rebuilt less stored) at 4x by
pixel repetition around the three places with the most new sources on refilled cores, all on one stretch.
"""
import csv
import sys
import numpy as np
from astropy.io import fits
from scipy.ndimage import gaussian_filter, maximum_filter
from scipy.spatial import cKDTree

R = 11
SEED = 2449


def lum_of(path):
    data = fits.getdata(path).astype(np.float64)
    return data.mean(axis=0) if data.ndim == 3 else data


def stars_of(path):
    with open(path, newline="") as f:
        rows = list(csv.DictReader(f))
    x = np.array([float(r["x"]) for r in rows])
    y = np.array([float(r["y"]) for r in rows])
    sig = np.array([float(r["significance"]) for r in rows])
    sub = np.array([r["outcome"] == "Subtracted" for r in rows])
    inp = np.array([r["inpainted"] in ("1", "True", "true") for r in rows])
    sat = np.array([r["saturated"] in ("1", "True", "true") for r in rows])
    return x, y, sig, sub, inp, sat


def core_level(lum, xs, ys):
    h, w = lum.shape
    out = np.full(len(xs), np.nan)
    oy, ox = np.mgrid[-R:R + 1, -R:R + 1]
    for k in range(0, len(xs), 4096):
        fx, fy = xs[k:k + 4096], ys[k:k + 4096]
        cx, cy = np.round(fx).astype(int), np.round(fy).astype(int)
        ok = (cx - R >= 0) & (cy - R >= 0) & (cx + R < w) & (cy + R < h)
        idx = np.nonzero(ok)[0]
        if len(idx) == 0:
            continue
        px = cx[idx, None, None] + ox[None]
        py = cy[idx, None, None] + oy[None]
        patch = lum[py, px]
        d2 = (px - fx[idx, None, None]) ** 2 + (py - fy[idx, None, None]) ** 2
        ring = (d2 >= 36) & (d2 <= 100)
        core = d2 <= 4.0
        bad = ~np.isfinite(patch) | (patch == 0)
        valid = ~(bad & (ring | core)).any(axis=(1, 2))
        med = np.nanmedian(np.where(ring, patch, np.nan).reshape(len(idx), -1), axis=1)
        mean = np.nanmean(np.where(core, patch, np.nan).reshape(len(idx), -1), axis=1)
        z = mean - med
        z[~valid] = np.nan
        out[k + idx] = z
    return out


def find(lum, s):
    present = np.isfinite(lum) & (lum != 0)
    filled = np.where(present, lum, np.nanmedian(lum[present]))
    f = gaussian_filter(filled, s) - gaussian_filter(filled, 6 * s)
    v = f[present]
    noise = 1.4826 * np.median(np.abs(v - np.median(v)))
    peak = (f == maximum_filter(f, size=5)) & (f > 5 * noise) & present
    py, px = np.nonzero(peak)
    return f, noise, px, py


master_path, old_plate, old_csv, new_plate, new_csv, fwhm = sys.argv[1:7]
out_png = sys.argv[7] if len(sys.argv) > 7 else None
s = float(fwhm) / 2.355
lo, ln = lum_of(old_plate), lum_of(new_plate)
xo, yo, sigo, subo, inpo, sato = stars_of(old_csv)
xn, yn, sign, subn, inpn, satn = stars_of(new_csv)
to = cKDTree(np.c_[xo, yo])
d, j = to.query(np.c_[xn, yn], distance_upper_bound=0.05)
m = np.isfinite(d)
print(f"stars: {len(xo):,} stored, {len(xn):,} rebuilt, {m.sum():,} at the same place")
jn = np.nonzero(m)[0]
jo = j[m]
both = subn[jn] & subo[jo] & ~satn[jn]
refilled = both & ~inpo[jo] & inpn[jn]
untouched = both & ~inpo[jo] & ~inpn[jn]
inboth = both & inpo[jo] & inpn[jn]
print(f"subtracted and unsaturated on both: {both.sum():,}; refilled {refilled.sum():,}, untouched {untouched.sum():,}, "
      f"inpainted on both {inboth.sum():,}")

rng = np.random.default_rng(SEED)
h, w = ln.shape
allx = np.r_[xo, xn]
ally = np.r_[yo, yn]
tall = cKDTree(np.c_[allx, ally])
rx = rng.uniform(R + 1, w - R - 2, 60000)
ry = rng.uniform(R + 1, h - R - 2, 60000)
free = tall.query(np.c_[rx, ry])[0] > 6
rx, ry = rx[free][:6000], ry[free][:6000]
null = {}
for lab, lum in (("stored", lo), ("rebuilt", ln)):
    z = core_level(lum, rx, ry)
    z = z[np.isfinite(z)]
    q = np.percentile(z, [0.5, 16, 50, 84, 99.5])
    null[lab] = q
    print(f"null on the {lab} plate: {len(z):,} star-free spots, median {q[2]:+.3e}, half the 16-84 spread {(q[3] - q[1]) / 2:.3e}")

zo = core_level(lo, xn[jn], yn[jn])
zn = core_level(ln, xn[jn], yn[jn])
sg = sign[jn]
print("cores against each plate's own null (bright over its 99.5th percentile, low under its 0.5th; noise gives 0.5 % each):")
for name, grp in (("refilled", refilled), ("untouched", untouched), ("inpainted on both", inboth)):
    for a, b in ((5, 10), (10, 20), (20, 50), (50, 100), (100, 1e9)):
        g = grp & (sg >= a) & (sg < b) & np.isfinite(zo) & np.isfinite(zn)
        if g.sum() < 50:
            continue
        w0 = (null["stored"][3] - null["stored"][1]) / 2
        w1 = (null["rebuilt"][3] - null["rebuilt"][1]) / 2
        print(f"  {name:<18} {a:>3g}-{b:<5g} n {g.sum():>7,}  bright {np.mean(zo[g] > null['stored'][4]) * 100:5.1f} -> {np.mean(zn[g] > null['rebuilt'][4]) * 100:5.1f} %"
              f"   low {np.mean(zo[g] < null['stored'][0]) * 100:5.1f} -> {np.mean(zn[g] < null['rebuilt'][0]) * 100:5.1f} %"
              f"   median level, in null widths {np.median(zo[g] - null['stored'][2]) / w0:+.2f} -> {np.median(zn[g] - null['rebuilt'][2]) / w1:+.2f}")

fo, no, pxo, pyo = find(lo, s)
fn, nn, pxn, pyn = find(ln, s)
print(f"sources over 5 sigma of the filtered plate: {len(pxo):,} stored (sigma {no:.3e}), {len(pxn):,} rebuilt (sigma {nn:.3e})")
tso = cKDTree(np.c_[pxo, pyo])
tsn = cKDTree(np.c_[pxn, pyn])
new_only = tso.query(np.c_[pxn, pyn])[0] > 1.5
old_only = tsn.query(np.c_[pxo, pyo])[0] > 1.5
print(f"  new on the rebuilt plate: {new_only.sum():,}; gone from the stored plate: {old_only.sum():,}")
rf = jn[refilled]
t_ref = cKDTree(np.c_[xn[rf], yn[rf]])
subs = np.nonzero(subn)[0]
t_sub = cKDTree(np.c_[xn[subs], yn[subs]])
qx, qy = pxn[new_only], pyn[new_only]
on_ref = t_ref.query(np.c_[qx, qy])[0] <= 2.5
at_sub = ~on_ref & (t_sub.query(np.c_[qx, qy])[0] <= 3.0)
away = ~on_ref & ~at_sub
was = fo[qy, qx] / no
now = fn[qy, qx] / nn
for name, g in (("on a refilled core (within 2.5 px)", on_ref), ("at another subtracted star (within 3 px)", at_sub), ("away from both", away)):
    if g.sum() == 0:
        continue
    print(f"  new, {name:<42} {g.sum():>6,}  stored value there: median {np.median(was[g]):+.1f} sigma, {np.mean(was[g] >= 4) * 100:4.1f} % were 4 or more, "
          f"{np.mean(was[g] < 2) * 100:4.1f} % under 2; rebuilt median {np.median(now[g]):.1f}")
gx, gy = pxo[old_only], pyo[old_only]
g_ref = t_ref.query(np.c_[gx, gy])[0] <= 2.5
print(f"  gone, on a refilled core: {g_ref.sum():,} of {old_only.sum():,}; their rebuilt value: median {np.median(fn[gy, gx][g_ref] / nn) if g_ref.any() else float('nan'):+.1f} sigma")
print(f"  refilled cores with a new source: {on_ref.sum():,} of {refilled.sum():,} ({on_ref.sum() / max(refilled.sum(), 1) * 100:.2f} %)")

if out_png:
    from PIL import Image
    lm = lum_of(master_path)
    bx, by = qx[on_ref], qy[on_ref]
    cell = 96
    hist, ye, xe = np.histogram2d(by, bx, bins=[np.arange(0, h + 1, cell), np.arange(0, w + 1, cell)])
    hist[0, :] = hist[-1, :] = 0
    hist[:, 0] = hist[:, -1] = 0
    rows = []
    for _ in range(3):
        iy, ix = np.unravel_index(np.argmax(hist), hist.shape)
        n_here = int(hist[iy, ix])
        hist[max(iy - 1, 0):iy + 2, max(ix - 1, 0):ix + 2] = 0
        y0, x0 = int(ye[iy]), int(xe[ix])
        sl = (slice(y0, y0 + cell), slice(x0, x0 + cell))
        med = np.median(lo[sl])
        sd = 1.4826 * np.median(np.abs(lo[sl] - med))
        lo_v, hi_v = med - 6 * sd, med + 14 * sd
        panels = []
        for img in (lm[sl], lo[sl], ln[sl]):
            panels.append(np.clip((img - lo_v) / (hi_v - lo_v), 0, 1))
        diff = (ln[sl] - lo[sl]) / (20 * sd) + 0.5
        panels.append(np.clip(diff, 0, 1))
        gap = np.ones((cell, 2))
        row = np.concatenate([p if i == 0 else np.concatenate([gap, p], axis=1) for i, p in enumerate(panels)], axis=1)
        rows.append(row)
        print(f"  look crop at x {x0}, y {y0}: {n_here} new sources on refilled cores in it")
    sheet = np.concatenate([r if i == 0 else np.concatenate([np.ones((2, r.shape[1])), r], axis=0) for i, r in enumerate(rows)], axis=0)
    sheet = np.repeat(np.repeat(sheet, 4, axis=0), 4, axis=1)
    Image.fromarray((sheet * 255).astype(np.uint8)).save(out_png)
    print(f"  wrote {out_png} (columns: master, stored plate, rebuilt plate, rebuilt less stored at 20 sigma to the half)")
