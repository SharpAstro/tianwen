"""How many of the store's masters hold a star like HIP 80609 (#2292): a star whose light still stands far out (2026-11-20).

    python -I bright_census.py <store> <out tsv> [stars per master, default 4]

Per session master (the flip variants and side files left out): the brightest compact peaks at least 420 px inside the
frame and 300 px apart, found on the frame read at every 4th row and column (a quarter of the file's pages, so a run
beside a trainer does not flood the file cache), then measured at full resolution in a crop round each: the ring medians
less the crop's own sky (its median at 300 to 400 px), on the channels' mean, in pixel sigma (neighbour differences of a
central 512 px crop). Rings: 8-22, 22-40, 40-60, 60-100, 100-140, 140-200, 200-300 px.

A halo is round and a nebula is not, so each ring also gets its SPREAD: the ring cut in 8 sectors, (largest sector median
less smallest) over the ring's median excess. Printed and written: one row a star, the largest 100-140 px excess first.
The injector draws a halo to 45 px and the plate builder cuts one flat at about 120 px, so a star that stands 5 sigma
over its sky at 100-140 px with a spread under 1 is one neither has ever shown a model."""
import os, sys, time
import numpy as np
from astropy.io import fits
from scipy.ndimage import maximum_filter, uniform_filter

STORE, OUT = sys.argv[1], sys.argv[2]
PER = int(sys.argv[3]) if len(sys.argv) > 3 else 4
EDGES = [8, 22, 40, 60, 100, 140, 200, 300]
R, STEP = 400, 4
yy, xx = np.mgrid[-R:R + 1, -R:R + 1]
dist = np.hypot(xx, yy); ang = np.arctan2(yy, xx)
sector = np.floor((ang + np.pi) / (2 * np.pi) * 8).clip(0, 7).astype(int)
sky_ring = (dist >= 300) & (dist < 400)
rings = [(dist >= lo) & (dist < hi) for lo, hi in zip(EDGES[:-1], EDGES[1:])]
masters = sorted(f for f in os.listdir(f"{STORE}/session-masters") if f.endswith(".fits") and "flip=" not in f)
rows = []
t0 = time.time()
with open(OUT, "w", encoding="utf-8") as out:
    out.write("session\tx\ty\tpeak_sigma\t" + "\t".join(f"r{lo}_{hi}" for lo, hi in zip(EDGES[:-1], EDGES[1:])) + "\tspread_100_140\tspread_140_200\n")
    for k, name in enumerate(masters):
        try:
            with fits.open(f"{STORE}/session-masters/{name}", memmap=True) as h:
                d = h[0].data
                if d is None or d.ndim not in (2, 3):
                    continue
                lum4 = np.asarray(d[..., ::STEP, ::STEP], np.float32)
                lum4 = lum4 if lum4.ndim == 2 else lum4[:3].mean(axis=0)
                H, W = d.shape[-2:]
                if min(H, W) < 2 * R + 200:
                    continue
                cy, cx = H // 2, W // 2
                c = np.asarray(d[..., cy - 256:cy + 256, cx - 256:cx + 256], np.float32)
                c = c if c.ndim == 2 else c[:3].mean(axis=0)
                sig = 1.4826 * float(np.nanmedian(np.abs(np.diff(c, axis=1)))) / np.sqrt(2)
                if not np.isfinite(sig) or sig <= 0:
                    continue
                ok4 = np.isfinite(lum4) & (lum4 != 0)
                L = np.where(ok4, lum4, np.nanmedian(lum4[ok4]))
                s = uniform_filter(L, 3)
                peak = (s == maximum_filter(s, 2 * (300 // STEP) + 1))
                m = R // STEP + 5
                peak[:m] = peak[-m:] = False; peak[:, :m] = peak[:, -m:] = False
                ys, xs = np.nonzero(peak)
                order = np.argsort(-s[ys, xs])[:PER]
                for j in order:
                    y0, x0 = int(ys[j]) * STEP, int(xs[j]) * STEP
                    crop = np.asarray(d[..., y0 - R - 8:y0 + R + 9, x0 - R - 8:x0 + R + 9], np.float32)
                    crop = crop if crop.ndim == 2 else crop[:3].mean(axis=0)
                    crop = np.nan_to_num(crop, nan=float(np.nanmedian(crop)))
                    inner = uniform_filter(crop, 3)[R:R + 17, R:R + 17]            # refine the centre within 8 px
                    dy, dx = np.unravel_index(int(np.argmax(inner)), inner.shape)
                    cut = crop[dy:dy + 2 * R + 1, dx:dx + 2 * R + 1]
                    sky = float(np.median(cut[sky_ring]))
                    vals = [(float(np.median(cut[r])) - sky) / sig for r in rings]
                    spread = []
                    for r in (rings[4], rings[5]):
                        sec = np.array([np.median(cut[r & (sector == q)]) - sky for q in range(8)]) / sig
                        med = float(np.median(cut[r]) - sky) / sig
                        spread.append(float((sec.max() - sec.min()) / med) if med > 0.5 else float("nan"))
                    row = (name[:-5], x0 - 8 + dx, y0 - 8 + dy, (float(cut[R, R]) - sky) / sig, vals, spread)
                    rows.append(row)
                    out.write(f"{row[0]}\t{row[1]}\t{row[2]}\t{row[3]:.0f}\t" + "\t".join(f"{v:.2f}" for v in vals) + f"\t{spread[0]:.2f}\t{spread[1]:.2f}\n")
                out.flush()
        except Exception as e:  # a master that cannot be read is named and the census goes on
            print(f"SKIPPED {name[:80]}: {type(e).__name__} {e}", flush=True)
        if (k + 1) % 25 == 0:
            print(f"{k + 1}/{len(masters)} masters, {len(rows)} stars, {time.time() - t0:.0f} s", flush=True)
rows.sort(key=lambda r: -r[4][4])
far = [r for r in rows if r[4][4] >= 5 and np.isfinite(r[5][0]) and r[5][0] < 1]
print(f"{len(masters)} masters, {len(rows)} stars measured; standing 5 sigma or more at 100-140 px with a spread under 1: {len(far)} stars in {len({r[0] for r in far})} masters")
print("session (60 chars)                                            x     y   peak   " + " ".join(f"{lo}-{hi}".rjust(8) for lo, hi in zip(EDGES[:-1], EDGES[1:])) + "   spread")
for r in rows[:40]:
    print(f"{r[0][:60]:60s} {r[1]:5d} {r[2]:5d} {r[3]:6.0f}   " + " ".join(f"{v:8.1f}" for v in r[4]) + f"   {r[5][0]:.2f} / {r[5][1]:.2f}")
