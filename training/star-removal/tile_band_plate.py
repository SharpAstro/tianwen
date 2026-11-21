"""Does an export's target tile carry its plate's 9 to 41 px band? (#2203, R4f's correction, 2026-11-20)
    python -I tile_band_plate.py <export from the stored plates> <export from the grain plates> <stored plates> <grain plates> <session substring>
Per target tile free of the knot defect's zero blocks: band.py's statistic (through a 7 px median, the 9 px box mean
less the 41 px one, rms over the inner 176 px, in sigma of a 9 px mean) of the PLATE over the tile's own footprint and of
the TILE, for the stored plates and for the grain plates. Green only, to keep the table narrow; medians for all three
channels at the end."""
import glob, json, os, sys
import numpy as np
from astropy.io import fits
from scipy.ndimage import median_filter, uniform_filter

EA, EB, PA, PB, KEY = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[5].lower()
T = 256


def band(t):
    out = []
    for c in t:
        c = np.nan_to_num(c, nan=float(np.nanmedian(c)))
        sig = 1.4826 * np.median(np.abs(np.diff(c, axis=1))) / np.sqrt(2)
        m = median_filter(c, 7)
        b = uniform_filter(m, 9) - uniform_filter(m, 41)
        out.append(float(np.sqrt(np.mean(b[40:-40, 40:-40] ** 2)) / max(sig / 9.0, 1e-12)))
    return np.array(out)


rows = [json.loads(l) for l in open(os.path.join(EA, "tiles-manifest.jsonl"), encoding="utf-8")]
rows = [r for r in rows if r["Frame"] == "master" and KEY in r["SessionId"].lower() and "flip=" not in r["SessionId"]]
stem = os.path.basename(os.path.dirname(rows[0]["Tile"]))
plate_a = np.asarray(fits.getdata(glob.glob(os.path.join(PA, glob.escape(stem) + "_plate.fits"))[0]), np.float32)
plate_b = np.asarray(fits.getdata(glob.glob(os.path.join(PB, glob.escape(stem) + "_plate.fits"))[0]), np.float32)
H, W = plate_a.shape[1:]
out = []
print("cell            plate, stored   tile, stored   plate, grain   tile, grain   (green)")
for r in rows:
    x, y = r["CellX"], r["CellY"]
    if x < 0 or y < 0 or x + T > W or y + T > H:
        continue
    ta = np.fromfile(os.path.join(EA, r["Tile"]), "<f2").astype(np.float32).reshape(-1, T, T)
    tb = np.fromfile(os.path.join(EB, r["Tile"]), "<f2").astype(np.float32).reshape(-1, T, T)
    if (ta == 0).all(axis=0).sum() >= 400 or (tb == 0).all(axis=0).sum() >= 400:
        continue
    pa, pb = plate_a[:, y:y + T, x:x + T], plate_b[:, y:y + T, x:x + T]
    if not np.isfinite(pa).all():
        continue
    v = (band(pa), band(ta), band(pb), band(tb))
    out.append(v)
    print(f"x{x:<5d} y{y:<5d}  {v[0][1]:10.1f}   {v[1][1]:10.1f}   {v[2][1]:10.1f}   {v[3][1]:10.1f}")
o = np.array(out)
f = lambda a: " / ".join(f"{x:.1f}" for x in a)
print(f"tiles read: {len(o)}")
for name, k in (("plate, stored", 0), ("tile, stored", 1), ("plate, grain", 2), ("tile, grain", 3)):
    print(f"   {name:14s} median {f(np.median(o[:, k], axis=0))}; 90th percentile {f(np.percentile(o[:, k], 90, axis=0))}")
print(f"   tile over plate, stored, per tile: median {f(np.median(o[:, 1] / o[:, 0], axis=0))}")
print(f"   tile over plate, grain, per tile: median {f(np.median(o[:, 3] / o[:, 2], axis=0))}")
