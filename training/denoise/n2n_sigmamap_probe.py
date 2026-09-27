"""Does a PER-PIXEL noise plane stop a model smoothing bright detail, with no retraining? (run log, "Detail kept")

Every model conditions on ONE number per tile, `with_sigma`: the MAD of the tile's darkest half, i.e. the SKY's
noise, broadcast over the whole plane. After the export stretch a bright core's noise is not the sky's, so a model
told "sky noise" there smooths structure as if it were noise. This probe gives the SAME checkpoints a plane that
varies with brightness and scores both, at full strength, with n2n_starsplit's detail-kept block and its removal.

The per-pixel noise is an ORACLE for the feasibility question: each field's own noise-against-level curve, measured
from its half pairs ((A - B) / sqrt 2 cancels the signal and leaves one half's noise; a robust sigma per level bin of
the master's low-pass, star-like peaks masked). At a pixel the plane is the tile's usual value times
curve(level) / curve(level of the tile's darkest half), with the level read from the INPUT's own low-pass, which is
what inference could do too. If an oracle curve does not give the detail back, a product-side estimator will not.

Usage:
  python n2n_sigmamap_probe.py --cache <eval cache> --only <session substring> --models slug=ckpt.pt [...]
"""
import argparse

import numpy as np
import torch
from scipy.ndimage import gaussian_filter

import n2n_metrics as M
import n2n_smoke as S
import n2n_starsplit as SS

LEVEL_EDGES = np.arange(0.15, 1.0001, 0.05)
RATIO_CLIP = (0.05, 4.0)


def noise_curve(lum_a, lum_b, lum_m, mask):
    """Per LEVEL_EDGES bin of the master's low-pass: 1.4826 x MAD of (A - B) / sqrt 2 over unmasked pixels, or NaN
    where a bin holds under 2000 pixels. Filled forward/backward so every level has a value."""
    d = (lum_a - lum_b) / np.sqrt(2.0)
    lvl = np.stack([gaussian_filter(x, SS.DETAIL_LEVEL_SIGMA) for x in lum_m])
    idx = np.digitize(lvl, LEVEL_EDGES)
    sig = np.full(len(LEVEL_EDGES) + 1, np.nan)
    for b in range(len(sig)):
        sel = (idx == b) & ~mask
        if sel.sum() >= 2000:
            v = d[sel]
            sig[b] = 1.4826 * float(np.median(np.abs(v - np.median(v))))
    good = np.where(~np.isnan(sig))[0]
    for b in range(len(sig)):
        if np.isnan(sig[b]):
            sig[b] = sig[good[np.argmin(np.abs(good - b))]]
    return sig


def sigma_plane(x, curve, smooth, absolute=False):
    """The per-pixel conditioning plane for a batch of UNCROPPED input tiles [N, C, H, W] (numpy).

    The curve is interpolated CONTINUOUSLY between bin centres and the plane low-passed at `smooth` px: a model
    that never saw a varying plane draws a stepped one's edges into the image as contour lines (the binned first
    version did exactly that on the Orion core, 2026-09-28)."""
    centres = np.concatenate([[LEVEL_EDGES[0] - 0.025], (LEVEL_EDGES[:-1] + LEVEL_EDGES[1:]) / 2, [LEVEL_EDGES[-1] + 0.025]])
    lum = x[:, :S.CH].mean(axis=1)
    out = np.empty(lum.shape, np.float32)
    for t in range(len(lum)):
        flat = lum[t].ravel()
        med, q25 = np.quantile(flat, 0.5), np.quantile(flat, 0.25)
        s_tile = (med - q25) * S.SIGMA_SCALE           # exactly bg_sigma_torch's value, per tile
        ref = np.interp(q25, centres, curve)             # the middle of the darkest half, where s_tile is measured
        lvl = gaussian_filter(lum[t], SS.DETAIL_LEVEL_SIGMA)
        if absolute:
            # The TRUE noise at each level, in the plane's own units: bg_sigma is (median - q25) of the darkest
            # half, which for pure Gaussian noise is 0.6745 sigma, so a true sigma maps to 0.6745 x sigma x scale.
            plane = 0.6745 * np.interp(lvl, centres, curve) * S.SIGMA_SCALE
        else:
            ratio = np.clip(np.interp(lvl, centres, curve) / ref, *RATIO_CLIP)
            plane = s_tile * ratio
        out[t] = gaussian_filter(plane, smooth, mode='nearest') if smooth > 0 else plane
    return out


def run(model, x, plane, dev, batch=16):
    res = []
    with torch.no_grad():
        for i in range(0, len(x), batch):
            xb = torch.from_numpy(x[i:i + batch]).to(dev)
            pb = torch.from_numpy(plane[i:i + batch]).to(dev).unsqueeze(1)
            res.append(model(torch.cat([xb, pb], dim=1)).cpu().numpy())
    return np.concatenate(res)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--cache', required=True)
    ap.add_argument('--only', required=True, help='comma-separated session substrings, as n2n_starsplit --only')
    ap.add_argument('--models', nargs='+', required=True, help='slug=checkpoint.pt')
    ap.add_argument('--png', default=None,
                    help='also write a labelled 1:1 sheet of the brightest cells: input, each model with the tile '
                         'sigma and with the pixel map, half B; one clip per row, from the input column')
    ap.add_argument('--png-cells', type=int, default=2)
    ap.add_argument('--smooth', type=float, default=8.0, help='low-pass of the plane in px (0 = none)')
    ap.add_argument('--absolute', action='store_true',
                    help='set the plane from the measured curve alone, not anchored to the tile\'s darkest-half MAD '
                         '(which reads a nebula-filled tile\'s texture as noise)')
    a = ap.parse_args()

    dev = torch.device('cuda' if torch.cuda.is_available() else 'cpu')
    mm, meta = S.open_cache(a.cache)
    wanted = [t.strip().lower() for t in a.only.split(',') if t.strip()]
    idx = [i for i in range(meta['train_cells'], meta['cells'])
           if meta['has_halves'][i] and any(t in meta['keys'][i][0].lower() for t in wanted)]
    print(f'{len(idx)} cells of {a.only!r}')
    half_a = np.asarray(mm[idx, S.SLOT_HALF_A], dtype=np.float32)
    half_b = np.asarray(mm[idx, S.SLOT_HALF_B], dtype=np.float32)
    master = np.asarray(mm[idx, S.SLOT_MASTER], dtype=np.float32)
    la, lb, lm = (S.crop(v).mean(axis=1) for v in (half_a, half_b, master))

    # Star-like peaks masked for the curve and the detail bins alike; every peak, since there is no Gaia split here.
    stars = M.star_table(lm)
    none = [(np.zeros(0, int), np.zeros(0, int), np.zeros(0))] * len(idx)
    bins = SS.detail_bins(lb, stars, none)
    curve = noise_curve(la, lb, lm, bins < 0)
    print('noise against level (stretched), relative to the 0.25 level:')
    ref = curve[np.digitize(0.25, LEVEL_EDGES)]
    print('  ' + '  '.join(f'{lo:.2f}:{curve[np.digitize(lo + 1e-6, LEVEL_EDGES)] / ref:.2f}' for lo in LEVEL_EDGES[::2]))

    d_a, d_b = SS.detail_bands(la), SS.detail_bands(lb)
    d_ref = SS.detail_reference(d_a, d_b, bins)
    base = float(np.mean([M.bg_stats(t)[1] for t in la]))
    plane_map = sigma_plane(half_a, curve, a.smooth, a.absolute)
    print(f'true noise at the 0.25 level in plane units: {0.6745 * ref * S.SIGMA_SCALE:.3f}')
    print(f'plane: per-tile constant median {np.median([np.quantile(p, 0.5) for p in plane_map]):.3f}; '
          f'per-pixel p5 {np.percentile(plane_map, 5):.3f} p50 {np.percentile(plane_map, 50):.3f} p95 {np.percentile(plane_map, 95):.3f}')
    print('detail kept: levels <0.30 | 0.30-0.45 | 0.45-0.60 | >=0.60, bands 0-1,1-2,2-4 px; removed = background noise removed')
    sheet_cols = [('input A', S.crop(half_a))]
    for spec in a.models:
        slug, ckpt = spec.split('=', 1)
        model, planes = S.load_model(a.cache, ckpt, dev)
        if planes != 1:
            print(f'{slug}: conditioning planes {planes}, not the scalar plane; skipped')
            continue
        const = S.crop(S.denoise(a.cache, ckpt, half_a, dev))
        pmap = S.crop(run(model, half_a, plane_map, dev))
        for view, out in (('tile sigma', const), ('pixel map', pmap)):
            lo = out.mean(axis=1)
            removed = (1.0 - float(np.mean([M.bg_stats(t)[1] for t in lo])) / base) * 100.0
            print(f'{slug:12s} {view:10s} removed {removed:5.1f}%  ' + SS.detail_line('', SS.detail_kept(lo, d_b, bins, d_ref)).split(':', 1)[1])
        sheet_cols += [(f'{slug} tile', const), (f'{slug} map', pmap)]
    sheet_cols.append(('half B (ref)', S.crop(half_b)))

    if a.png:
        from PIL import Image as PImage, ImageDraw
        bright = [(float((gaussian_filter(lb[t], SS.DETAIL_LEVEL_SIGMA) > SS.DETAIL_LEVELS[-1]).mean()), t) for t in range(len(idx))]
        rows = [t for _, t in sorted(bright, reverse=True)[:a.png_cells]]
        h, w = sheet_cols[0][1].shape[2:]
        banner = 18
        img = PImage.new('RGB', (w * len(sheet_cols), banner + h * len(rows)), (16, 16, 16))
        draw = ImageDraw.Draw(img)
        for c, (label, _) in enumerate(sheet_cols):
            draw.text((c * w + 6, 4), label, fill=(235, 235, 235))
        for r, t in enumerate(rows):
            lo, hi = np.percentile(sheet_cols[0][1][t], (0.5, 99.8))
            for c, (_, arr) in enumerate(sheet_cols):
                tile = np.clip((arr[t].transpose(1, 2, 0) - lo) / max(hi - lo, 1e-6), 0, 1)
                img.paste(PImage.fromarray((tile * 255).astype(np.uint8)), (c * w, banner + r * h))
        img.save(a.png)
        print(f'wrote {a.png} ({img.width}x{img.height}, 1:1; rows are the {len(rows)} cells with the most pixels above '
              f'{SS.DETAIL_LEVELS[-1]:g})')


if __name__ == '__main__':
    main()
