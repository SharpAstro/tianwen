"""Read-only: what would the #1777 demagenta do to a linear colour master with NO star split? (#1824)

`ScreenedStars.Demagenta` is G' = max(G, min(R, B)): green rises to the lesser of red and blue where both
exceed it. Shipped, it runs on the stars plate of a star split. This measures the alternative the issue asks
about, run on the master itself, as

  A  the whole frame            rise = dG
  B  weighted by the star mask  rise = w * dG, w = max(STARMASK, STARMASK feathered by a Gaussian)

with dG = max(0, min(R, B) - G), reported in sigma of the RMS map (`tianwen image sources --maps` writes it:
the noise of the DETECTION channel, green on a colour frame, which is the channel demagenta moves).

    tianwen image sources <master.fits> --maps
    python tools/master-quality/demagenta.py <master.fits> [--sidecars DIR] [--out DIR] [--feather 1.0]
    python tools/master-quality/demagenta.py --self-test

The sidecars are read by the stem `SourceDetectionWriter.SidecarPath` gives (`.fits.gz` / `.fit.fz` lose both
parts): `<stem>.starmask.fits`, `.structmask.fits`, `.skymask.fits`, `.rms.fits`. The master's first image HDU
is read as planes R, G, B; a pixel that is NaN in any plane or exactly zero in all three (the canvas ring no
frame covered) is left out of every figure.

Per variant and per region (star mask, structure mask, sky mask, as the sidecars give them: the star mask
carries its margin, so it can overlap structure) it prints the pixel count, the fraction whose green rises by
more than 1 and 3 sigma, and the median and 99th percentile of the rise. For B it also prints the magenta left
just OUTSIDE the star mask, a ring 1 to 4 px past it: the unweighted dG there (what is present), what B removed,
and the residual, so a halo the mask misses shows. On pure noise A already lifts about 11 percent of sky
pixels by more than 1 sigma (min(R, B) - G has mean -0.56 and sd 1.30 sigma, since min of two noisy channels
sits below one): that bias is a cost of the whole-frame variant, and the sky rows show it.

Writes before / after PNG crops to --out (default `demagenta-<stem>/` in the CURRENT directory), each row
three panels (before | A | B) through ONE colour asinh stretch, its black point and white from the BEFORE
frame, so the eye compares like with like: the brightest star, the brightest structure, the sky patch A moves
most, and the whole frame downsampled. Needs `Pillow` for them (`--no-png` skips)."""
import argparse
import math
import os
import sys
import tempfile

import numpy as np
from astropy.io import fits

LUMA = (0.2126, 0.7152, 0.0722)  # Rec.709, ScreenedStars.LumaWeights
RING_PX = (1, 4)  # the ring past the star mask, in px
SUFFIXES = {
    'star': '.starmask.fits',
    'struct': '.structmask.fits',
    'sky': '.skymask.fits',
    'rms': '.rms.fits',
}


# ---------------------------------------------------------------- input

def sidecar_path(master, suffix, directory=None):
    """SourceDetectionWriter.SidecarPath: the frame's name less .gz/.fz and the extension, the suffix appended."""
    name = os.path.basename(master)
    ext = os.path.splitext(name)[1]
    if ext.lower() in ('.gz', '.fz'):
        name = name[:-len(ext)]
        ext = os.path.splitext(name)[1]
    stem = name[:-len(ext)] if ext else name
    return os.path.join(directory if directory is not None else os.path.dirname(master), stem + suffix)


def first_image(path):
    """The first HDU holding an image of at least two axes, as float32."""
    with fits.open(path, memmap=False) as hdul:
        for hdu in hdul:
            if hdu.data is not None and hdu.data.ndim >= 2 and hdu.data.size > 0:
                return np.asarray(hdu.data, dtype=np.float32)
    raise SystemExit(f'{path}: no image HDU')


def load_master(path):
    data = first_image(path)
    if data.ndim != 3 or data.shape[0] != 3:
        raise SystemExit(f'{path}: expected 3 planes (R, G, B), got shape {data.shape}')
    return data[0], data[1], data[2]


def load_sidecars(master, directory):
    out = {}
    for key, suffix in SUFFIXES.items():
        p = sidecar_path(master, suffix, directory)
        if not os.path.exists(p):
            raise SystemExit(f'missing sidecar {p} (run: tianwen image sources "{master}" --maps)')
        d = first_image(p)
        if d.ndim != 2:
            d = d.reshape(d.shape[-2:])
        out[key] = d if key == 'rms' else d > 0.5
    return out


# ---------------------------------------------------------------- pixel helpers (numpy only)

def shifted_or(mask, dy, dx):
    """OR `mask` shifted by (dy, dx) into a new array (what falls off the frame is dropped)."""
    h, w = mask.shape
    out = np.zeros_like(mask)
    out[max(dy, 0):h + min(dy, 0), max(dx, 0):w + min(dx, 0)] = \
        mask[max(-dy, 0):h + min(-dy, 0), max(-dx, 0):w + min(-dx, 0)]
    return out


def dilate(mask, radius):
    """Disc dilation by `radius` px."""
    if radius <= 0:
        return mask.copy()
    out = mask.copy()
    r = int(radius)
    for dy in range(-r, r + 1):
        for dx in range(-r, r + 1):
            if (dy or dx) and dy * dy + dx * dx <= radius * radius:
                out |= shifted_or(mask, dy, dx)
    return out


def gaussian_blur(a, sigma):
    """Separable Gaussian, edges replicated."""
    if sigma <= 0:
        return a
    r = int(math.ceil(3 * sigma))
    xs = np.arange(-r, r + 1, dtype=np.float32)
    k = np.exp(-xs * xs / (2 * sigma * sigma))
    k /= k.sum()
    for axis in (0, 1):
        pad = [(r, r) if ax == axis else (0, 0) for ax in (0, 1)]
        p = np.pad(a, pad, mode='edge')
        out = np.zeros_like(a)
        n = a.shape[axis]
        for i, wgt in enumerate(k):
            out += wgt * (p[i:i + n, :] if axis == 0 else p[:, i:i + n])
        a = out
    return a


def star_weight(star, feather):
    """1 inside the star mask, its Gaussian-feathered edge outside it."""
    m = star.astype(np.float32)
    return np.maximum(m, np.clip(gaussian_blur(m, feather), 0.0, 1.0))


# ---------------------------------------------------------------- the measurement

def region_stats(rise, sel):
    """(n, fraction > 1 sigma, fraction > 3 sigma, median, p99) of `rise` over `sel`."""
    v = rise[sel]
    if v.size == 0:
        return (0, math.nan, math.nan, math.nan, math.nan)
    return (int(v.size), float((v > 1).mean()), float((v > 3).mean()),
            float(np.median(v)), float(np.percentile(v, 99)))


def measure(R, G, B, rms, star, struct, sky, feather):
    """Everything the report prints, plus the arrays the crops need."""
    valid = np.isfinite(R) & np.isfinite(G) & np.isfinite(B) & ~((R == 0) & (G == 0) & (B == 0))
    valid &= np.isfinite(rms) & (rms > 0)
    d_g = np.where(valid, np.maximum(0.0, np.minimum(R, B) - G), 0.0).astype(np.float32)
    w = star_weight(star, feather)
    safe_rms = np.where(valid, rms, 1.0)
    rise_a = d_g / safe_rms
    rise_b = w * d_g / safe_rms
    regions = {'star mask': star & valid, 'structure mask': struct & valid, 'sky mask': sky & valid}
    result = {'valid': valid, 'd_g': d_g, 'w': w, 'rise_a': rise_a, 'rise_b': rise_b, 'regions': {}}
    for name, sel in regions.items():
        result['regions'][name] = {'A': region_stats(rise_a, sel), 'B': region_stats(rise_b, sel)}
    ring = dilate(star, RING_PX[1]) & ~dilate(star, RING_PX[0] - 1) & valid
    rings = {'ring 1-4 px': ring, 'ring, structure excluded': ring & ~struct}
    result['ring'] = {}
    for name, sel in rings.items():
        result['ring'][name] = {
            'present': region_stats(rise_a, sel),  # the unweighted dG: the magenta there
            'removed': region_stats(rise_b, sel),
            'residual': region_stats(rise_a - rise_b, sel),
        }
    return result


def fmt_row(label, s):
    n, f1, f3, med, p99 = s
    return f'  {label:<26} {n:>10d}  {100 * f1:>8.2f}%  {100 * f3:>8.2f}%  {med:>8.3f}  {p99:>8.3f}'


def report(result, feather, out=print):
    head = f'  {"":<26} {"pixels":>10}  {">1 sigma":>9}  {">3 sigma":>9}  {"median":>8}  {"p99":>8}'
    out(f'green rise, in sigma of the RMS map; star weight = mask feathered by a Gaussian of sigma {feather:g} px')
    for variant, title in (('A', 'A, whole frame'), ('B', 'B, weighted by the star mask')):
        out(f'\n{title}')
        out(head)
        for name, r in result['regions'].items():
            out(fmt_row(name, r[variant]))
    out('\nB, magenta just OUTSIDE the star mask (a ring 1 to 4 px past it; rise in sigma)')
    out(head)
    for name, r in result['ring'].items():
        for what in ('present', 'removed', 'residual'):
            out(fmt_row(f'{name}: {what}', r[what]))


# ---------------------------------------------------------------- crops

def luma(R, G, B):
    return LUMA[0] * R + LUMA[1] * G + LUMA[2] * B


class Stretch:
    """The colour asinh of the shipped star render, its black point per channel and its white taken ONCE from
    the BEFORE frame and applied to before and after alike: every channel times the one factor its
    luminance gives, then scaled down as a whole when it passes 1, so a core keeps its hue."""

    def __init__(self, planes, sel_sky, valid, soft_sigma, rms_sky):
        self.black = [float(np.median(p[sel_sky & valid])) if (sel_sky & valid).any() else 0.0 for p in planes]
        self.soft = max(soft_sigma * rms_sky, 1e-12)
        sample = (slice(None, None, 4), slice(None, None, 4))
        lum = luma(*[p[sample] - b for p, b in zip(planes, self.black)])
        lum = lum[valid[sample]]
        self.white = float(np.percentile(lum, 99.9)) if lum.size else 1.0
        self.white = max(self.white, 10 * self.soft)
        self.norm = math.asinh(self.white / self.soft)

    def __call__(self, R, G, B):
        v = [np.maximum(np.nan_to_num(p) - b, 0.0) for p, b in zip((R, G, B), self.black)]
        y = luma(*v)
        factor = np.where(y > 0, np.arcsinh(y / self.soft) / self.norm / np.maximum(y, 1e-30), 0.0)
        v = [c * factor for c in v]
        top = np.maximum(np.maximum(v[0], v[1]), np.maximum(v[2], 1.0))
        return np.stack([c / top for c in v], axis=-1)


def best_block(score, ok_mask, sizes, min_fraction):
    """(y, x, size) of the top-left of the block with the highest mean score among those where at least
    `min_fraction` of the pixels are in `ok_mask`; the first of `sizes` that has one (a small frame has no big block)."""
    h, w = score.shape
    for size in sizes:
        hb, wb = h // size, w // size
        if hb == 0 or wb == 0:
            continue
        s = score[:hb * size, :wb * size].reshape(hb, size, wb, size).mean(axis=(1, 3))
        ok = ok_mask[:hb * size, :wb * size].reshape(hb, size, wb, size).mean(axis=(1, 3)) >= min_fraction
        if ok.any():
            by, bx = np.unravel_index(np.argmax(np.where(ok, s, -np.inf)), s.shape)
            return int(by) * size, int(bx) * size, size
    return None


def centred(cy, cx, size, h, w):
    y0 = int(np.clip(cy - size // 2, 0, max(h - size, 0)))
    x0 = int(np.clip(cx - size // 2, 0, max(w - size, 0)))
    return slice(y0, min(y0 + size, h)), slice(x0, min(x0 + size, w))


def render_crops(R, G, B, result, star, struct, sky, rms, soft_sigma, outdir, stem):
    from PIL import Image, ImageDraw
    os.makedirs(outdir, exist_ok=True)
    valid = result['valid']
    h, w = R.shape
    rms_sky = float(np.median(rms[sky & valid])) if (sky & valid).any() else float(np.nanmedian(rms))
    stretch = Stretch((R, G, B), sky, valid, soft_sigma, rms_sky)
    d_g, wgt = result['d_g'], result['w']
    lum = luma(np.where(valid, R, 0.0), np.where(valid, G, 0.0), np.where(valid, B, 0.0)).astype(np.float32)
    written = []

    def panels(sl, title, scale, down=1):
        def after(weight):
            return (R[sl], G[sl] + weight[sl] * d_g[sl], B[sl])
        trio = []
        for label, planes in (('before', (R[sl], G[sl], B[sl])),
                              ('A whole frame', after(np.ones_like(wgt))),
                              ('B star-weighted', after(wgt))):
            if down > 1:
                planes = tuple(block_mean(p, down) for p in planes)
            img = (np.clip(stretch(*planes), 0, 1) * 255 + 0.5).astype(np.uint8)
            im = Image.fromarray(img, 'RGB')
            if scale > 1:
                im = im.resize((im.width * scale, im.height * scale), Image.NEAREST)
            trio.append((label, im))
        band = 22
        gap = 4
        pw, ph = trio[0][1].size
        sheet = Image.new('RGB', (3 * pw + 2 * gap, ph + band), (24, 24, 24))
        draw = ImageDraw.Draw(sheet)
        for i, (label, im) in enumerate(trio):
            sheet.paste(im, (i * (pw + gap), band))
            draw.text((i * (pw + gap) + 4, 5), f'{title}: {label}', fill=(235, 235, 235))
        path = os.path.join(outdir, f'{stem}_{title.split(",")[0].replace(" ", "_")}.png')
        sheet.save(path)
        written.append(path)

    star_ok = star & valid
    if star_ok.any():
        g_in = np.where(star_ok, G, -np.inf)
        cy, cx = np.unravel_index(np.argmax(g_in), g_in.shape)
        panels(centred(cy, cx, 96, h, w), 'brightest star, 96 px at 4x', 4)
    struct_ok = struct & valid
    found = best_block(np.where(struct_ok, lum - float(np.median(lum[valid])), 0.0), struct_ok, (64, 32), 0.5) \
        if struct_ok.any() else None
    if found:
        y, x, size = found
        panels(centred(y + size // 2, x + size // 2, 4 * size, h, w), f'brightest structure, {4 * size} px at 2x', 2)
    sky_ok = sky & valid
    found = best_block(np.where(sky_ok, d_g / np.where(valid, rms, 1.0), 0.0), sky_ok, (128, 64, 32), 0.999) \
        if sky_ok.any() else None
    if found:
        y, x, size = found
        scale = max(1, 384 // size)
        panels(centred(y + size // 2, x + size // 2, size, h, w), f'sky patch A moves most, {size} px at {scale}x', scale)
    down = max(1, int(math.ceil(w / 900)))
    panels((slice(0, h), slice(0, w)), f'whole frame, 1/{down}', 1, down)
    return written


def block_mean(a, k):
    h, w = a.shape
    hb, wb = h // k, w // k
    return a[:hb * k, :wb * k].reshape(hb, k, wb, k).mean(axis=(1, 3))


# ---------------------------------------------------------------- driver

def run(master, sidecars=None, out=None, feather=1.0, soft_sigma=3.0, png=True, say=print):
    R, G, B = load_master(master)
    sc = load_sidecars(master, sidecars)
    for k, v in sc.items():
        if v.shape != R.shape:
            raise SystemExit(f'sidecar {k} is {v.shape}, the master {R.shape}')
    result = measure(R, G, B, sc['rms'], sc['star'], sc['struct'], sc['sky'], feather)
    say(f'{master}  {R.shape[1]}x{R.shape[0]}, {int(result["valid"].sum())} valid px')
    report(result, feather, say)
    if png:
        stem = os.path.basename(master).split('.')[0]
        outdir = out or f'demagenta-{stem}'
        for p in render_crops(R, G, B, result, sc['star'], sc['struct'], sc['sky'], sc['rms'],
                              soft_sigma, outdir, stem):
            say(f'wrote {p}')
    return result


# ---------------------------------------------------------------- self test

def synthetic():
    """A 240 px frame, sigma 0.01 noise on a 0.1 level in every channel: a magenta star with a magenta halo
    just past its mask, a blue sky patch with a red region over it (red over blue is magenta: R and B both
    above G), a red region alone, and neutral sky."""
    n, sigma = 240, 0.01
    rng = np.random.default_rng(1824)
    planes = [np.full((n, n), 0.1, np.float32) + rng.normal(0, sigma, (n, n)).astype(np.float32) for _ in range(3)]
    R, G, B = planes
    yy, xx = np.mgrid[0:n, 0:n]
    r = np.hypot(yy - 60, xx - 60)
    core, halo = r <= 3, (r > 6) & (r <= 8)
    R[core] += 0.9; B[core] += 0.9; G[core] += 0.2       # magenta core: dG = 0.7
    R[halo] += 0.08; B[halo] += 0.08                       # magenta halo outside the mask: dG = 8 sigma
    B[120:, 120:] += 0.3                                   # blue sky patch
    R[140:200, 150:210] += 0.3                             # red over blue: R = B = 0.4, G = 0.1, dG = 0.3
    R[140:200, 10:70] += 0.3                               # red alone
    star = dilate(core, 3)                                 # the margin sources writes: 3 px
    struct = np.zeros((n, n), bool)
    struct[140:200, 150:210] = True
    struct[140:200, 10:70] = True
    sky = ~(star | struct)
    rms = np.full((n, n), sigma, np.float32)
    # the canvas ring: exact zero in every plane, and one NaN
    for p in planes:
        p[:4, :] = 0; p[-4:, :] = 0; p[:, :4] = 0; p[:, -4:] = 0
    R[100, 100] = np.nan
    return (R, G, B), rms, star, struct, sky


def self_test():
    (R, G, B), rms, star, struct, sky = synthetic()
    res = measure(R, G, B, rms, star, struct, sky, feather=1.0)
    a, b = res['rise_a'], res['rise_b']
    n = R.shape[0]
    yy, xx = np.mgrid[0:n, 0:n]
    far = res['valid'] & (np.hypot(yy - 60, xx - 60) > 14)
    checks = []

    def check(name, ok, detail=''):
        checks.append(ok)
        print(('ok   ' if ok else 'FAIL ') + name + (f'  [{detail}]' if detail else ''))

    def window(ys, xs):
        m = np.zeros((n, n), bool)
        m[ys, xs] = True
        return m & far

    core = res['valid'] & (np.hypot(yy - 60, xx - 60) <= 3)
    check('the canvas ring and the NaN are left out', not res['valid'][:4].any() and not res['valid'][100, 100])
    check('magenta star core: A and B both rise by more than 3 sigma',
          (a[core] > 3).all() and (b[core] > 3).all(), f'A {np.median(a[core]):.1f}, B {np.median(b[core]):.1f}')
    overlap = window(slice(150, 190), slice(160, 200))
    check('red over blue: A rises about 30 sigma, B moves nothing',
          abs(np.median(a[overlap]) - 30) < 3 and b[overlap].max() == 0,
          f'A {np.median(a[overlap]):.1f}, B max {b[overlap].max():.3f}')
    red_only = window(slice(150, 190), slice(20, 60))
    # one colour alone: min(R, B) is the quiet channel, so only the difference of two noisy channels is left
    # (sd 1.41 sigma, 1.7 percent over 3), nothing like the 30 sigma of a real magenta
    red_only = window(slice(150, 190), slice(20, 60))
    check('red alone: A moves it by noise only (median under half a sigma, under 3 percent by 3 sigma), B not at all',
          np.median(a[red_only]) < 0.5 and (a[red_only] > 3).mean() < 0.03 and b[red_only].max() == 0)
    blue_only = window(slice(122, 136), slice(130, 230))
    check('blue sky alone: A moves it by noise only, B not at all',
          np.median(a[blue_only]) < 0.5 and (a[blue_only] > 3).mean() < 0.03 and b[blue_only].max() == 0)
    neutral = window(slice(10, 50), slice(125, 230))
    f1, f3 = (a[neutral] > 1).mean(), (a[neutral] > 3).mean()
    check('neutral sky: A lifts 5 to 20 percent by 1 sigma (the min-of-two noise bias), under 1 percent by 3',
          0.05 < f1 < 0.20 and f3 < 0.01, f'{100 * f1:.1f}%, {100 * f3:.2f}%')
    check('neutral sky: B moves nothing', b[neutral].max() == 0)
    ring = res['ring']['ring 1-4 px']
    check('the halo past the mask shows as residual magenta in the ring',
          ring['residual'][1] > 0.2 and ring['residual'][0] > 50,
          f'residual >1 sigma {100 * ring["residual"][1]:.0f}% of {ring["residual"][0]} px')
    check('the feather removes some of it', ring['removed'][3] >= 0 and ring['removed'][1] > 0)

    with tempfile.TemporaryDirectory() as tmp:  # the file path: a master, its sidecars beside it, the PNGs
        master = os.path.join(tmp, 'synthetic.fits')
        fits.PrimaryHDU(np.stack([R, G, B]).astype(np.float32)).writeto(master)
        for key, data in (('star', star), ('struct', struct), ('sky', sky), ('rms', rms)):
            arr = data.astype(np.float32 if key == 'rms' else np.uint8)
            fits.PrimaryHDU(arr).writeto(sidecar_path(master, SUFFIXES[key]))
        lines = []
        res2 = run(master, out=os.path.join(tmp, 'png'), say=lines.append)
        check('the files path agrees with the arrays path', np.allclose(res2['rise_a'], a) and np.allclose(res2['rise_b'], b))
        check('the sidecar stem drops .gz and the extension',
              sidecar_path('/x/a.fits.gz', '.rms.fits') == os.path.join('/x', 'a.rms.fits')
              and sidecar_path('a.fit.fz', '.rms.fits', 'd') == os.path.join('d', 'a.rms.fits'))
        try:
            import PIL  # noqa: F401
            pngs = sorted(os.listdir(os.path.join(tmp, 'png')))
            check('four crops written', len(pngs) == 4, ', '.join(pngs))
        except ImportError:
            print('skip crops: Pillow is not installed')
        print('\n'.join(lines))
    if not all(checks):
        sys.exit('self-test FAILED')
    print('self-test passed')


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0], formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('master', nargs='?', help='linear 3-plane master FITS (R, G, B)')
    ap.add_argument('--sidecars', help="folder of `tianwen image sources --maps` sidecars (default: beside the master)")
    ap.add_argument('--out', help='folder for the PNG crops (default ./demagenta-<stem>)')
    ap.add_argument('--feather', type=float, default=1.0, help="sigma, px, of the Gaussian feathering the star mask's edge (default 1; 0 = binary)")
    ap.add_argument('--soft', type=float, default=3.0, help="the crops' asinh softening, in sky sigmas (default 3)")
    ap.add_argument('--no-png', action='store_true', help='print the table only')
    ap.add_argument('--self-test', action='store_true', help='check the maths on a synthetic frame and exit')
    args = ap.parse_args()
    if args.self_test:
        self_test()
        return
    if not args.master:
        ap.error('a master FITS or --self-test')
    run(args.master, args.sidecars, args.out, args.feather, args.soft, not args.no_png)


if __name__ == '__main__':
    main()
