"""One image per kept core for a second look at the contact sheets (#2201, #2360): a 2 by 2 sheet of four views, with no
name on it, so that whoever judges it sees the picture alone.
    python tiles.py <survey.jsonl> <session-masters folder> <probe out dir (the weights)> <tiles out dir>
A: the contact sheet's crop (220 px a side at 1:1, or wider to hold the core), asinh stretch. B: the same with the mask's
0.5 contour in red. C: the same crop linear from its 2nd percentile to its brightest pixel. D: four times wider, asinh,
with the contour. tiles.tsv beside them names each tile's master; an image carries only a letter per view. The numbers
are the contact sheets' (masters in the order of their names, then a master's cores in the survey's order).
View C misleads where a star is in the crop: linear to the brightest pixel shows the star and nothing else, so a core
fainter than its stars reads as no core. The second look of 2026-11-21 called such tiles stars only."""
import json, os, sys
import numpy as np
from astropy.io import fits
from PIL import Image, ImageDraw

SURVEY, MASTERS, WEIGHTS, OUT = sys.argv[1:5]
P = 768
os.makedirs(OUT, exist_ok=True)


def square(a, cy, cx, half, fill):
    """The square of 2 * half about (cy, cx) of a[..., H, W], with `fill` where it leaves the frame."""
    H, W = a.shape[-2:]
    out = np.full(a.shape[:-2] + (2 * half, 2 * half), fill, a.dtype)
    y0, y1, x0, x1 = max(0, cy - half), min(H, cy + half), max(0, cx - half), min(W, cx + half)
    out[..., y0 - (cy - half):y1 - (cy - half), x0 - (cx - half):x1 - (cx - half)] = a[..., y0:y1, x0:x1]
    return out


def to_image(x):
    x = x if x.shape[0] == 3 else np.repeat(x[:1], 3, 0)
    return Image.fromarray((255 * np.clip(x, 0, 1)).astype(np.uint8).transpose(1, 2, 0)).resize((P, P), Image.LANCZOS)


def asinh_view(crop, inside):
    v = crop[:, inside]
    lo, hi = np.percentile(v, 2), np.percentile(v, 99.9)
    x = np.clip((crop - lo) / max(hi - lo, 1e-12), 0, 1)
    return to_image(np.arcsinh(x * 20) / np.arcsinh(20) * inside)


def linear_view(crop, inside):
    v = crop[:, inside]
    lo, hi = np.percentile(v, 2), float(v.max())
    return to_image(np.clip((crop - lo) / max(hi - lo, 1e-12), 0, 1) * inside)


def contour(im, m):
    """The mask's edge drawn at the panel's own size, two pixels wide, so a wide view keeps a line that shows."""
    mm = np.asarray(Image.fromarray(m.astype(np.uint8) * 255).resize((P, P), Image.NEAREST)) > 0
    p = np.pad(mm, 1)
    whole = p[1:-1, 1:-1] & p[:-2, 1:-1] & p[2:, 1:-1] & p[1:-1, :-2] & p[1:-1, 2:]
    e = np.pad(mm & ~whole, 1)
    e = e[1:-1, 1:-1] | e[:-2, 1:-1] | e[2:, 1:-1] | e[1:-1, :-2] | e[1:-1, 2:]
    a = np.asarray(im).copy()
    a[e] = (255, 40, 40)
    return Image.fromarray(a)


rows = sorted((r for r in (json.loads(l) for l in open(SURVEY, encoding="utf-8")) if r["cores"]), key=lambda r: r["stem"])
tsv = open(os.path.join(OUT, "tiles.tsv"), "w", encoding="utf-8")
tsv.write("n\tpeak_ratio\tcore_px\tX\tY\tcrop_px\tstem\n")
n = 0
for r in rows:
    d = np.asarray(fits.getdata(os.path.join(MASTERS, r["stem"] + ".fits")), np.float32)
    d = np.nan_to_num(d[None] if d.ndim == 2 else d)
    H, W = r["Height"], r["Width"]
    w = np.fromfile(os.path.join(WEIGHTS, r["stem"] + ".weight.u8"), np.uint8).reshape(H, W) >= 128
    frame = np.ones((H, W), bool)
    for c in r["cores"]:
        n += 1
        half = int(max(110, 1.6 * np.sqrt(c["CoreAreaPx"])))
        cy, cx = c["Y"], c["X"]
        crop, m, ins = square(d, cy, cx, half, 0), square(w, cy, cx, half, False), square(frame, cy, cx, half, False)
        wide, wm, wins = square(d, cy, cx, 4 * half, 0), square(w, cy, cx, 4 * half, False), square(frame, cy, cx, 4 * half, False)
        a = asinh_view(crop, ins)
        sheet = Image.new("RGB", (2 * P + 4, 2 * P + 4), (18, 18, 18))
        dr = ImageDraw.Draw(sheet)
        for i, im in enumerate((a, contour(a, m), linear_view(crop, ins), contour(asinh_view(wide, wins), wm))):
            x, y = (i % 2) * (P + 4), (i // 2) * (P + 4)
            sheet.paste(im, (x, y))
            dr.rectangle((x, y, x + 22, y + 20), fill=(18, 18, 18))
            dr.text((x + 8, y + 4), "ABCD"[i], fill=(255, 255, 255))
        sheet.save(os.path.join(OUT, f"tile-{n:02d}.jpg"), quality=92)
        tsv.write(f"{n}\t{c['peakRatio']:.2f}\t{c['CoreAreaPx']}\t{cx}\t{cy}\t{2 * half}\t{r['stem']}\n")
        tsv.flush()
print("tiles written", n)
