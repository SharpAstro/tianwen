"""Old vs new bake master, three PNGs for eyeballing the Bayer-phase pattern:
  <name>_1_full.png         whole frame, 1/3 scale, colour stretch
  <name>_2_sky_2x.png       a 256 px star-poor sky patch at 2x, colour stretch
  <name>_3_sky_pixels.png   a 48 px patch at 10x, each channel in its own sigma units (-2.5..+2.5)
Both images use the OLD master's per-channel background sigma, so noise and pattern amplitude compare
directly. Usage: python tools/master-quality/compare_masters.py <old.fits> <new.fits> <name> <outdir>"""
import sys
import numpy as np
from astropy.io import fits
from PIL import Image, ImageDraw

old_path, new_path, name, outdir = sys.argv[1:5]


def load(p):
    d = fits.getdata(p).astype(np.float64)
    return np.where(np.isfinite(d), d, np.nan)


old, new = load(old_path), load(new_path)
assert old.shape == new.shape, (old.shape, new.shape)
C, H, W = old.shape


def bg_sigma(img, region=None):
    meds, sigs = [], []
    for c in range(C):
        p = img[c] if region is None else img[c][region]
        p = p[np.isfinite(p)]
        lo, hi = np.percentile(p, [5, 55])
        b = p[(p >= lo) & (p <= hi)]
        m = np.median(p[p <= hi])
        meds.append(m)
        sigs.append(1.4826 * np.median(np.abs(b - np.median(b))))
    return np.array(meds), np.array(sigs)


cy, cx = slice(H // 5, 4 * H // 5), slice(W // 5, 4 * W // 5)
old_meds, old_sig = bg_sigma(old, (cy, cx))
new_meds, _ = bg_sigma(new, (cy, cx))


def colour(img, meds, sig, gain=12.0):
    out = np.empty((img.shape[1], img.shape[2], 3))
    for c in range(C):
        x = (img[c] - meds[c]) / (sig[c] * gain)
        out[..., c] = np.arcsinh(np.clip(x, -0.5, None) * 3) / np.arcsinh(3 * 40)
    out = np.nan_to_num(np.clip(out * 1.6 + 0.08, 0, 1))
    return (out * 255).astype(np.uint8)


def label(im, text):
    d = ImageDraw.Draw(im)
    d.rectangle([0, 0, 8 * len(text) + 12, 18], fill=(0, 0, 0))
    d.text((6, 3), text, fill=(255, 255, 0))
    return im


def side_by_side(a, b, gap=8):
    out = Image.new("RGB", (a.width + b.width + gap, max(a.height, b.height)), (40, 40, 40))
    out.paste(a, (0, 0))
    out.paste(b, (a.width + gap, 0))
    return out


# 1. full frame at 1/3 scale
def downsample(img, k=3):
    h, w = (img.shape[1] // k) * k, (img.shape[2] // k) * k
    return np.nanmean(img[:, :h, :w].reshape(C, h // k, k, w // k, k), axis=(2, 4))


full = side_by_side(label(Image.fromarray(colour(downsample(old), old_meds, old_sig / 3)), "OLD 2026-09-16 bake"),
                    label(Image.fromarray(colour(downsample(new), new_meds, old_sig / 3)), "NEW sky reference"))
full.save(f"{outdir}/{name}_1_full.png")

# 2. pick a star-poor sky patch in the central region: lowest p99 of green among low-median blocks
g = old[1]
best = None
B = 256
for y in range(H // 5, 4 * H // 5 - B, B // 2):
    for x in range(W // 5, 4 * W // 5 - B, B // 2):
        blk = g[y:y + B, x:x + B]
        if not np.isfinite(blk).all():
            continue
        score = (np.median(blk), np.percentile(blk, 99.5))
        if best is None or score[0] + 3 * (score[1] - score[0]) < best[0]:
            best = (score[0] + 3 * (score[1] - score[0]), y, x)
_, py, px = best
sky = (slice(py, py + B), slice(px, px + B))


def crop_colour(img, meds):
    c = colour(img[:, sky[0], sky[1]], meds, old_sig, gain=6.0)
    return Image.fromarray(c).resize((B * 2, B * 2), Image.NEAREST)


side_by_side(label(crop_colour(old, old_meds), f"OLD  sky x={px} y={py} 256px @2x"),
             label(crop_colour(new, new_meds), "NEW")).save(f"{outdir}/{name}_2_sky_2x.png")

# 3. 48 px at 10x, per channel in sigma units of that patch
P, Z = 48, 10
cy0, cx0 = py + B // 2 - P // 2, px + B // 2 - P // 2
patch = (slice(cy0, cy0 + P), slice(cx0, cx0 + P))


def sigma_tiles(img, title):
    tiles = []
    for c in range(C):
        p = img[c][patch]
        s = old_sig[c]
        x = np.clip((p - np.median(p)) / s, -2.5, 2.5)
        t = ((x + 2.5) / 5.0 * 255).astype(np.uint8)
        tiles.append(label(Image.fromarray(t).convert("RGB").resize((P * Z, P * Z), Image.NEAREST), f"{title} {'RGB'[c]}"))
    row = Image.new("RGB", (3 * P * Z + 16, P * Z), (40, 40, 40))
    for i, t in enumerate(tiles):
        row.paste(t, (i * (P * Z + 8), 0))
    return row


a, b = sigma_tiles(old, "OLD"), sigma_tiles(new, "NEW")
grid = Image.new("RGB", (a.width, a.height + b.height + 8), (40, 40, 40))
grid.paste(a, (0, 0))
grid.paste(b, (0, a.height + 8))
grid.save(f"{outdir}/{name}_3_sky_pixels.png")
print("patch", px, py, "old sigma", old_sig)
