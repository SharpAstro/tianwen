"""Before/after figure for the hot-pixel mask, cropped where it actually acted.

The open question from the bad-pixel survey is not "did the mask change pixels" (measured: 2119, in
617 diff clusters) but "did the visible drizzled CLUSTERS clear". Those are different quantities and
a full-frame view answers neither: at 3038x3034 a 36x25 px defect is a third of a percent of the
width and invisible once downsampled to something viewable.

So this locates the biggest changes from the FITS, then crops BOTH masters at the same coordinates
and tiles them, which is the only view where "gone" and "still there" look different.

Crops are stretched from the two frames' COMBINED percentiles, never per panel. A per-panel stretch
renormalises each crop to its own extremes, which makes a removed defect pull the whole panel's
brightness and shows a difference even when the pixels are identical.
"""
import numpy as np
from astropy.io import fits
from PIL import Image, ImageDraw
from scipy.ndimage import label, maximum_filter

BASE = r"C:\tianwen-scratch\bpm-probe"
CROP = 128          # px around each cluster centroid
COLS = 4
SCALE = 2           # nearest-neighbour upscale, so single defective pixels stay visible
TOP = 8


def read(path):
    with fits.open(path, memmap=False) as hdul:
        d = np.asarray(hdul[0].data, dtype=np.float32)
    return d if d.ndim == 3 else d[None]


def stretch(a, lo, hi):
    """Shared-range asinh. Diagnostic, NOT the pipeline's render: this figure is about geometry.

    NaN is painted as 0 rather than left to propagate, so an uncovered drizzle cell reads as empty
    sky instead of poisoning the crop it lands in.
    """
    x = np.clip((np.nan_to_num(a, nan=lo) - lo) / max(hi - lo, 1e-9), 0, 1)
    return np.arcsinh(x * 12.0) / np.arcsinh(12.0)


def to_rgb(cube, y, x, lo, hi):
    c = cube[:, y:y + CROP, x:x + CROP]
    if c.shape[0] >= 3:
        rgb = np.stack([stretch(c[i], lo[i], hi[i]) for i in range(3)], axis=-1)
    else:
        g = stretch(c[0], lo[0], hi[0])
        rgb = np.stack([g] * 3, axis=-1)
    return (np.clip(rgb, 0, 1) * 255).astype(np.uint8)


def main():
    nm = read(f"{BASE}/master_nomask.fits")
    mk = read(f"{BASE}/master_masked.fits")
    print(f"shapes {nm.shape} / {mk.shape}")

    lum_n, lum_m = nm.mean(axis=0), mk.mean(axis=0)

    # PER CHANNEL, against that channel's own MAD, then OR the three. Doing this on the luminance
    # instead reports ZERO: a defect living in one channel is divided by three when the channels are
    # averaged, while the luminance MAD only falls by about sqrt(3), so an 8-MAD single-channel
    # defect lands near 4.6 MAD in luminance and never crosses. The C# probe counts per channel and
    # found 2119 px; a figure that silently used a different statistic disagreed with it and looked
    # like the mask had done nothing.
    # NaN-AWARE THROUGHOUT. A drizzled master carries zero-weight holes plus the union-canvas
    # margin: 2,956,010 px here, 10.7% of the frame. A plain np.median over that returns NaN, every
    # threshold becomes NaN, and `> NaN` is false everywhere -- which reads as "the mask changed
    # nothing" and is indistinguishable from a real null result. Two passes of this figure said
    # exactly that before the NaN was found.
    nan_px = int(np.isnan(nm).sum())
    print(f"  NaN in nomask: {nan_px} px ({nan_px / nm.size * 100:.1f}%)")

    hot = np.zeros(nm.shape[1:], dtype=bool)
    for ch in range(nm.shape[0]):
        med = float(np.nanmedian(mk[ch]))
        mad = float(np.nanmedian(np.abs(mk[ch] - med))) + 1e-12
        delta = nm[ch] - mk[ch]
        ch_hot = np.isfinite(delta) & (delta > 8.0 * mad)
        print(f"  ch{ch}: median {med:.6g} mad {mad:.6g} -> {int(ch_hot.sum())} px changed")
        hot |= ch_hot
    lab, n = label(hot)
    print(f"{int(hot.sum())} px changed in {n} clusters (per-channel 8 MAD, OR-ed)")
    if n == 0:
        raise SystemExit("nothing changed; nothing to show")

    sizes = np.bincount(lab.ravel())
    sizes[0] = 0
    order = np.argsort(sizes)[::-1][:TOP]

    # One shared range across BOTH frames, from the unmasked one's robust stats per channel.
    lo, hi = [], []
    for ch in range(min(3, nm.shape[0])):
        m = float(np.nanmedian(nm[ch]))
        s = float(np.nanmedian(np.abs(nm[ch] - m))) + 1e-12
        lo.append(m - 2 * s)
        hi.append(m + 60 * s)

    rows = (len(order) + COLS - 1) // COLS
    pad, head, capt = 8, 46, 20
    cw = CROP * SCALE
    panel_w = cw * 2 + 6
    W = COLS * (panel_w + pad) + pad
    H = head + rows * (cw + capt + pad) + pad
    canvas = Image.new("RGB", (W, H), (16, 16, 20))
    d = ImageDraw.Draw(canvas)
    d.text((pad, 10), "Hot-pixel mask, cropped at the 8 largest changes. LEFT of each pair = no "
                      "mask, RIGHT = masked. Same session, same frames, same stretch.",
           fill=(235, 235, 235))
    d.text((pad, 26), f"2025-12-28 Segaull+Thors_Helmet, ASI533MC Pro g121, 90 subs, BayerDrizzle. "
                      f"{int(hot.sum())} px changed in {n} clusters.", fill=(150, 150, 160))

    for i, cid in enumerate(order):
        ys, xs = np.nonzero(lab == cid)
        cy, cx = int(ys.mean()), int(xs.mean())
        y0 = max(0, min(cy - CROP // 2, nm.shape[1] - CROP))
        x0 = max(0, min(cx - CROP // 2, nm.shape[2] - CROP))

        a = Image.fromarray(to_rgb(nm, y0, x0, lo, hi)).resize((cw, cw), Image.NEAREST)
        b = Image.fromarray(to_rgb(mk, y0, x0, lo, hi)).resize((cw, cw), Image.NEAREST)

        r, c = divmod(i, COLS)
        px = pad + c * (panel_w + pad)
        py = head + r * (cw + capt + pad)
        canvas.paste(a, (px, py))
        canvas.paste(b, (px + cw + 6, py))
        d.rectangle([px + cw + 2, py, px + cw + 4, py + cw], fill=(90, 90, 100))
        d.text((px, py + cw + 4),
               f"({x0},{y0})  {int(sizes[cid])} px changed", fill=(170, 170, 180))

    out = f"{BASE}/hotpix-before-after.png"
    canvas.save(out)
    print(f"wrote {out}  ({canvas.size[0]}x{canvas.size[1]})")

    # Whole-frame residual check: are there still isolated bright non-stellar peaks AFTER masking?
    for name, lum in (("nomask", lum_n), ("masked", lum_m)):
        m = float(np.nanmedian(lum))
        s = float(np.nanmedian(np.abs(lum - m))) + 1e-12
        peaks = np.isfinite(lum) & (lum >= maximum_filter(np.nan_to_num(lum, nan=-1e30), size=5)) & (lum > m + 30 * s)
        print(f"{name}: {int(peaks.sum())} isolated peaks above 30 MAD")


if __name__ == "__main__":
    main()
