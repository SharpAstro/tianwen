"""Render a bake master with its absent pixels marked, NaN in pink.

Two panels per master: the whole frame downsampled (hole mask taken by block-ANY so a single
scattered pixel survives the downsample) and a 1:1 crop on the largest hole component.
Zero-ring pixels are marked separately in blue, because the ring is what poisons the exporter's
min-anchored stretch gate and the holes are what shred the crop rectangle; they are different
defects and the point is to see them apart.
"""
import sys
import numpy as np
from astropy.io import fits
from PIL import Image as PILImage
from scipy import ndimage

PINK = np.array([255, 0, 170], dtype=np.uint8)
BLUE = np.array([0, 160, 255], dtype=np.uint8)


def read_cube(path):
    with fits.open(path, memmap=False) as hdul:
        for hdu in hdul:
            if hdu.data is not None and hdu.data.ndim >= 2:
                data = np.asarray(hdu.data, dtype=np.float32)
                break
        else:
            raise SystemExit(f"no image HDU in {path}")
    if data.ndim == 2:
        data = data[None, ...]
    if data.ndim == 3 and data.shape[-1] in (3, 4) and data.shape[0] > 4:
        data = np.moveaxis(data, -1, 0)
    return data  # (C, H, W)


def mtf(x, m):
    """The midtones transfer function, the same shape Image.StretchValue uses."""
    num = (m - 1.0) * x
    den = (2.0 * m - 1.0) * x - m
    out = np.divide(num, den, out=np.zeros_like(x), where=den != 0)
    return np.clip(out, 0.0, 1.0)


def stretch_rgb(cube, target=0.22):
    """Linked background-anchored MTF, NaN-robust, for looking at.

    Linked so a colour cast is not invented per channel; the point of these pictures is where the
    absent pixels are, and per-channel normalisation put red/cyan fringes on every clipped core.
    """
    n_ch = min(3, cube.shape[0])
    vals = cube[0][np.isfinite(cube[0])]
    lo = float(np.percentile(vals, 0.5))
    hi = float(np.percentile(vals, 99.8))
    span = max(hi - lo, 1e-9)
    chans = []
    for c in range(n_ch):
        n = np.clip((cube[c] - lo) / span, 0.0, 1.0)
        med = float(np.nanmedian(n))
        med = min(max(med, 1e-6), 0.999)
        # m carrying `med` to `target`: solve mtf(med; m) == target.
        m = (med * (target - 1.0)) / (2.0 * target * med - target - med)
        m = min(max(m, 1e-5), 0.9999)
        chans.append(mtf(np.nan_to_num(n, nan=0.0), m))
    while len(chans) < 3:
        chans.append(chans[0])
    # Grey on purpose: this is a defect map, and a dual-narrowband frame's own magenta would be
    # confusable with the magenta the absent pixels are painted in.
    grey = np.clip(chans[0] * 0.25 + chans[1] * 0.6 + chans[2] * 0.15, 0, 1)
    return (np.repeat(grey[..., None], 3, axis=-1) * 255).astype(np.uint8)


def block_any(mask, f):
    h, w = mask.shape
    h2, w2 = (h // f) * f, (w // f) * f
    return mask[:h2, :w2].reshape(h2 // f, f, w2 // f, f).any(axis=(1, 3))


def run(path, label, out_prefix, full_width=1400):
    cube = read_cube(path)
    c, h, w = cube.shape
    nan_per_ch = [int(np.isnan(cube[i]).sum()) for i in range(c)]
    nan_any = np.isnan(cube).any(axis=0)
    zero_all = (cube == 0).all(axis=0)

    # Interior vs border-reachable, the distinction issue #250 turns on.
    absent = nan_any | zero_all
    lbl, n = ndimage.label(absent)
    border_ids = set(np.unique(np.concatenate([lbl[0], lbl[-1], lbl[:, 0], lbl[:, -1]]))) - {0}
    interior = np.isin(lbl, list(set(range(1, n + 1)) - border_ids))
    holes = interior & nan_any
    hlbl, hn = ndimage.label(holes)

    print(f"=== {label}")
    print(f"    {w} x {h} x {c}")
    print(f"    NaN per channel      : {nan_per_ch}")
    print(f"    absent (NaN or zero) : {int(absent.sum()):,} px ({absent.mean()*100:.3f}%)")
    print(f"    interior NaN holes   : {int(holes.sum()):,} px in {hn} components")
    print(f"    exact-zero ring      : {int(zero_all.sum()):,} px")

    # Is a hole a coverage gap or a carved core? A gap sits in ordinary sky; a carved core sits in a
    # ring of near-saturated pixels. Ask the pixels immediately around each component.
    if holes.any():
        ring = ndimage.binary_dilation(holes, np.ones((5, 5), bool)) & ~holes
        finite0 = cube[0][np.isfinite(cube[0])]
        sky = float(np.median(finite0))
        top = float(np.percentile(finite0, 99.99))
        vals = cube[0][ring]
        vals = vals[np.isfinite(vals)]
        if vals.size:
            print(f"    sky median / p99.99 : {sky:.5g} / {top:.5g}")
            print(f"    rim around the holes : median {np.median(vals):.5g}, "
                  f"{100.0 * np.mean(vals >= top):.1f}% at or above p99.99")

    if out_prefix == "-":
        return nan_per_ch

    rgb = stretch_rgb(cube)

    # Full view: downsample the picture, block-ANY the masks so one pixel still shows.
    f = max(1, int(round(w / full_width)))
    small = np.array(PILImage.fromarray(rgb).resize((w // f, h // f), PILImage.LANCZOS))
    hs = block_any(holes, f)
    zs = block_any(zero_all, f)
    hs = ndimage.binary_dilation(hs, np.ones((3, 3), bool))
    zs = ndimage.binary_dilation(zs, np.ones((2, 2), bool))
    sh = min(small.shape[0], hs.shape[0])
    sw = min(small.shape[1], hs.shape[1])
    small[:sh, :sw][zs[:sh, :sw]] = BLUE
    small[:sh, :sw][hs[:sh, :sw]] = PINK
    PILImage.fromarray(small).save(f"{out_prefix}-full.png")

    # 1:1 crop on the largest hole component.
    if hn:
        sizes = ndimage.sum(holes, hlbl, range(1, hn + 1))
        big = int(np.argmax(sizes)) + 1
        ys, xs = np.where(hlbl == big)
        cy, cx = int(ys.mean()), int(xs.mean())
        half = 200
        y0, x0 = max(0, cy - half), max(0, cx - half)
        y1, x1 = min(h, y0 + 2 * half), min(w, x0 + 2 * half)
        crop = rgb[y0:y1, x0:x1].copy()
        cm = holes[y0:y1, x0:x1]
        crop[cm] = PINK
        PILImage.fromarray(crop).resize(((x1 - x0) * 2, (y1 - y0) * 2), PILImage.NEAREST).save(
            f"{out_prefix}-zoom.png")
        print(f"    largest component    : {int(sizes[big-1])} px at ({cx}, {cy}), zoom is 400 px at 2:1")
    return nan_per_ch


if __name__ == "__main__":
    run(sys.argv[1], sys.argv[2], sys.argv[3])
