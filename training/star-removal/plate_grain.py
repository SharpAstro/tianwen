"""The grain of unresolved stars out of a starless plate's crowded sky, the nebula kept (#2203 step 2 prototype, 2026-11-18).

    python -I plate_grain.py <plates in> <plates out> <worker k> <of n>

For every <stem>_plate.fits of <plates in> (worker k of n takes every n-th), writes <plates out>/<stem>_plate.fits and
one line to <plates out>/grain-<k>.log. The side files are not written here. Per channel:

  p7    = the plate through a 7 px median (a point source goes, anything wider stays)
  base  = p7 through a 31 px median (on the 2 x 2 binned plane): the smooth glow
  d     = p7 - base: the detail between the two scales, grain and nebula alike
  grain = how strong d is where nothing but stars makes it: per 100 px block of crowded sky its robust spread over the
          pixel noise; the lower quartile of each fifth of the blocks by star density; a line in sqrt(density)
  keep  = 0 where the rms of d over 21 px is under T grain, 1 from 2 T (T = 2): a nebula is strong over an area, grain
          is weak everywhere
  w     = the star density's weight (subtracted stars per pixel in a 61 px box: 0 under 0.003, 1 from 0.008)
  out   = the plate where w (1 - keep) is 0, base plus pixel noise where it is 1.

A plate with under eight crowded blocks is copied as it is. Absent pixels (NaN) stay absent.
"""
import csv, glob, os, sys, time, zlib
import numpy as np
from astropy.io import fits
from scipy.ndimage import uniform_filter, median_filter, zoom

SRC, DST, K_, N_ = sys.argv[1], sys.argv[2], int(sys.argv[3]), int(sys.argv[4])
T = 2.0
os.makedirs(DST, exist_ok=True)
log = open(f"{DST}/grain-{K_}.log", "a", encoding="utf-8")


def pixel_noise(a):
    return 1.4826 * np.median(np.abs(np.diff(a, axis=1))) / np.sqrt(2)


def noise_map(a, B=64):
    H, W = a.shape
    g = np.array([[pixel_noise(a[y:y + B, x:x + B]) for x in range(0, W, B)] for y in range(0, H, B)], np.float32)
    g = np.where(g > 0, g, np.median(g[g > 0]) if (g > 0).any() else 0)
    return zoom(g, (H / g.shape[0], W / g.shape[1]), order=1)[:H, :W]


def base_of(p7):
    H, W = p7.shape; h2, w2 = H // 2 * 2, W // 2 * 2
    small = p7[:h2, :w2].reshape(h2 // 2, 2, w2 // 2, 2).mean(axis=(1, 3))
    return zoom(median_filter(small, 15), (H / small.shape[0], W / small.shape[1]), order=1)[:H, :W]


files = sorted(glob.glob(f"{SRC}/*_plate.fits"))[K_::N_]
for path in files:
    stem = os.path.basename(path)[:-len("_plate.fits")]; dst = f"{DST}/{stem}_plate.fits"
    if os.path.exists(dst):
        continue
    t0 = time.time()
    with fits.open(path) as h:
        data = np.asarray(h[0].data, np.float32); header = h[0].header
    mono = data.ndim == 2
    plate = data[None] if mono else data
    C, H, W = plate.shape
    dens = np.zeros((H, W), np.float32)
    for s in csv.DictReader(open(f"{SRC}/{stem}_plate.stars.csv", encoding="utf-8")):
        if s["outcome"] == "Subtracted":
            x, y = int(round(float(s["x"]))), int(round(float(s["y"])))
            if 0 <= x < W and 0 <= y < H: dens[y, x] += 1
    dens = uniform_filter(dens, 61); w = np.clip((dens - 0.003) / 0.005, 0, 1); sq = np.sqrt(np.maximum(dens, 0))
    out = plate.copy(); note = []
    if w.max() > 0:
        for c in range(C):
            nan = ~np.isfinite(plate[c]); P = np.where(nan, np.nanmedian(plate[c]), plate[c])
            p7 = median_filter(P, 7); base = base_of(p7); d = p7 - base; sig = noise_map(P)
            r, dd = [], []
            for y in range(0, H - 100, 100):
                for x in range(0, W - 100, 100):
                    if w[y:y + 100, x:x + 100].mean() > 0.5 and not nan[y:y + 100, x:x + 100].any():
                        blk = d[y:y + 100, x:x + 100]
                        r.append(1.4826 * np.median(np.abs(blk - np.median(blk))) / max(float(sig[y + 50, x + 50]), 1e-12)); dd.append(dens[y:y + 100, x:x + 100].mean())
            if len(r) < 8:
                note.append("few"); continue
            r, dd = np.array(r), np.array(dd); q = np.quantile(dd, np.linspace(0, 1, 6)); xs, ys = [], []
            for k in range(5):
                m = (dd >= q[k]) & (dd <= q[k + 1])
                if m.sum() >= 2: xs.append(np.sqrt(np.median(dd[m]))); ys.append(np.percentile(r[m], 25))
            if len(set(np.round(xs, 6))) >= 3:
                fit = np.polyfit(xs, ys, 1); g = np.maximum(np.polyval(fit, sq), min(ys))
            else:
                g = np.full((H, W), np.percentile(r, 25), np.float32)
            t = T * g * sig
            a = np.sqrt(uniform_filter(d * d, 21)); keep = np.clip((a - t) / np.maximum(t, 1e-12), 0, 1)
            share = w * (1 - keep)
            rng = np.random.default_rng(zlib.crc32(f"{stem}|{c}".encode()))
            res = share * (base + rng.normal(0, 1, base.shape).astype(np.float32) * sig) + (1 - share) * P
            out[c] = np.where(nan, plate[c], res).astype(np.float32)
            note.append(f"grain {np.percentile(r, 25):.3f} smoothed {100 * share.mean():.1f}%")
    fits.PrimaryHDU(out[0] if mono else out, header).writeto(dst + ".tmp", overwrite=True); os.replace(dst + ".tmp", dst)
    log.write(f"{stem}\t{C}x{H}x{W}\tcrowded {100 * (w > 0.5).mean():.1f}%\t{' | '.join(note) or 'untouched'}\t{time.time() - t0:.0f}s\n"); log.flush()
