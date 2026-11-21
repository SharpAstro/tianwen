"""What the grain rule took from a plate, looked at (#2203, before R4f is registered, 2026-11-20).
    python -I grain_look.py <stored plates> <grain plates> <folder>/<sheet name> <stem substring> [<stem substring> ...]
The sheet is written as <folder>/grain-look-<sheet name>.png (as run on DE1 the folder was D:/temp/tianwen-scratch/r4-look,
written into the script then; it is part of the third argument since the script came into the tree, 2026-11-21).
Per plate two 300 px cells at 2x: the cell where the rule took the most broad structure (the rms of stored less grain
through a 9 px box), and the cell at the median of that measure among the cells it touched. Three pictures a cell on one
stretch (black the stored crop's 1st percentile, white its 99.7th, asinh): the stored plate, the grain plate, and what
was taken (stored less grain, about mid grey, at 4 times the contrast). Above each row: the cell, the taken structure's
rms in pixel noise, and the plate's own structure there (the stored crop's 9 px box less its 61 px box, rms in pixel
noise), so a taken figure can be read against what there was."""
import glob, os, sys
import numpy as np
from astropy.io import fits
from scipy.ndimage import uniform_filter
from PIL import Image, ImageDraw

SRC, DST = sys.argv[1], sys.argv[2]
OUT, NAME = os.path.dirname(sys.argv[3]) or ".", os.path.basename(sys.argv[3])
B, Z = 300, 2


def load(path):
    a = np.asarray(fits.getdata(path), np.float32)
    return a[None] if a.ndim == 2 else a[:3]


def noise(a):
    d = np.diff(a, axis=1)
    return 1.4826 * np.nanmedian(np.abs(d)) / np.sqrt(2)


rows = []
for key in sys.argv[4:]:
    hits = [p for p in sorted(glob.glob(f"{SRC}/*_plate.fits")) if key.lower() in os.path.basename(p).lower()]
    if not hits:
        print("no plate matches", key); continue
    path = hits[0]; stem = os.path.basename(path)[:-len("_plate.fits")]
    a, b = load(path), load(f"{DST}/{stem}_plate.fits")
    C, H, W = a.shape
    lum_a, lum_b = np.nanmean(a, axis=0), np.nanmean(b, axis=0)
    ok = np.isfinite(lum_a) & np.isfinite(lum_b)
    sig = noise(np.where(ok, lum_a, np.nan))
    taken = uniform_filter(np.where(ok, lum_a - lum_b, 0), 9)
    own = uniform_filter(np.where(ok, lum_a, np.nanmedian(lum_a)), 9) - uniform_filter(np.where(ok, lum_a, np.nanmedian(lum_a)), 61)
    cells = []
    for y in range(0, H - B + 1, B):
        for x in range(0, W - B + 1, B):
            if ok[y:y + B, x:x + B].mean() < 0.98:
                continue
            t = float(np.sqrt(np.mean(taken[y:y + B, x:x + B] ** 2)) / sig)
            o = float(np.sqrt(np.mean(own[y:y + B, x:x + B] ** 2)) / sig)
            changed = float((np.abs(lum_a - lum_b)[y:y + B, x:x + B] > 0).mean())
            cells.append((t, o, changed, y, x))
    touched = [c for c in cells if c[2] > 0.2]
    if not touched:
        print(stem[:80], "untouched"); continue
    touched.sort()
    picks = [("most taken", touched[-1]), ("median taken", touched[len(touched) // 2])]
    ts = np.array([c[0] for c in touched]); os_ = np.array([c[1] for c in touched])
    print(f"{stem[:90]}\n   cells touched {len(touched)} of {len(cells)}; taken structure rms, in pixel noise: median {np.median(ts):.2f}, p90 {np.percentile(ts, 90):.2f}, max {ts.max():.2f}; "
          f"the plate's own structure there: median {np.median(os_):.2f}, p90 {np.percentile(os_, 90):.2f}; taken over own, median {np.median(ts / np.maximum(os_, 1e-9)):.2f}")
    for label, (t, o, ch, y, x) in picks:
        ca, cb = np.nan_to_num(a[:, y:y + B, x:x + B]), np.nan_to_num(b[:, y:y + B, x:x + B])
        if C == 1:
            ca, cb = np.repeat(ca, 3, 0), np.repeat(cb, 3, 0)
        lo = np.percentile(ca.reshape(3, -1), 1, axis=1)[:, None, None]; hi = np.percentile(ca.reshape(3, -1), 99.7, axis=1)[:, None, None]
        hi = np.maximum(hi, lo + 1e-9)
        f = lambda v: (255 * np.arcsinh(np.clip((v - lo) / (hi - lo), 0, 1) * 10) / np.arcsinh(10)).astype(np.uint8).transpose(1, 2, 0)
        diff = np.clip(0.5 + 4 * (ca - cb) / (hi - lo), 0, 1)
        imgs = [f(ca), f(cb), (255 * diff).astype(np.uint8).transpose(1, 2, 0)]
        rows.append((f"{stem[:70]}  cell y{y} x{x}, {label}: taken {t:.2f}, the plate's own {o:.2f} (pixel noise)", imgs))

S = B * Z
sheet = Image.new("RGB", (3 * (S + 6) + 6, len(rows) * (S + 26) + 6), (20, 20, 20)); dr = ImageDraw.Draw(sheet)
for r, (title, imgs) in enumerate(rows):
    dr.text((6, 8 + r * (S + 26)), title + "   |   stored, grain rule, taken x4", fill=(230, 230, 230))
    for c, im in enumerate(imgs):
        sheet.paste(Image.fromarray(im).resize((S, S), Image.NEAREST), (6 + c * (S + 6), 24 + r * (S + 26)))
os.makedirs(OUT, exist_ok=True)
out = f"{OUT}/grain-look-{NAME}.png"; sheet.save(out); print("wrote", out, sheet.size)
