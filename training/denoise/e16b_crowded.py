"""E16b's crowded-field check: does a model blow a crowded field of unresolved stars out?

Registered in docs/plans/denoiser-training.md, "E16b, a crowded-field check and the reading of a null", before any E16b
model was scored. The registered scorer (n2n_starsplit.py) masks star-like peaks before it measures detail, so a model
can smooth a crowded core into a blob, or lift it toward clipping, and pass every column. This scores, against the
independent half B and with n2n_starsplit's own bands and formulas:

  CROWDED  the 40 cells of the Sgr Star Cloud with the most half-B peaks, every pixel inside the rim, no star mask;
  CORES    M22 and M28 (Sgr frame) and NGC 362 (SMC frame), the pixels within 3 half-light radii of each centre.

Measures, per population: grain kept and error left in the 0-1 and 1-2 px bands, the level (mean Y over mean B) per
channel, the clip share (luminance at or above 0.95, the output's minus B's) and stars kept (half B's peaks, Y's
amplitude above the tile median over A's). References: the input A and a Gaussian sigma 1 of A.

Usage:
  python e16b_crowded.py --cache <n2n-e16b-crowded> --bake <store> --models slug=ckpt.pt [...] [--plane-truth-anchor]
"""
import argparse
import os
import re
import warnings

import numpy as np
import torch
from scipy.ndimage import gaussian_filter

import n2n_metrics as M
import n2n_smoke as S
import n2n_starsplit as SP

CROWDED_SESSION = 'Sagittarius-Star-Cloud/2022-07-29'
CROWDED_CELLS = 40
BANDS = 2                       # the 0-1 and 1-2 px bands of SP.DETAIL_BANDS
CLIP_LEVEL = 0.95
# Catalogue centre (RA, Dec, degrees), half-light radius (arcmin, Harris 2010), and the session holding it.
CORES = {
    'M22': (279.09975, -23.90475, 3.36, 'Sagittarius-Star-Cloud/2022-07-29'),
    'M28': (276.13704, -24.86985, 0.98, 'Sagittarius-Star-Cloud/2022-07-29'),
    'NGC 362': (15.80942, -70.84878, 0.82, 'Small-Magellanic-Cloud/2026-08-01'),
}
CORE_RADII = 3.0


def core_pixels(bake, sid, ra, dec, r_arcmin):
    """(x, y, radius) of a catalogue position in the session master's own pixel frame, by its WCS."""
    from astropy.io import fits
    from astropy.wcs import WCS
    name = re.sub(r'[<>:"/\\|?*]', '_', sid) + '.fits'
    header = fits.getheader(os.path.join(bake, 'session-masters', name), 0)
    with warnings.catch_warnings():
        warnings.simplefilter('ignore')
        w = WCS(header, naxis=2)
        x, y = w.all_world2pix([[ra, dec]], 0)[0]
        scale = abs(w.proj_plane_pixel_scales()[0].value) * 3600.0
    return float(x), float(y), r_arcmin * 60.0 / scale


def measures(a, b, y, mask):
    """The registered measures over the pixels `mask` [n, H, W] of cropped [n, C, H, W] tiles."""
    la, lb, ly = a.mean(axis=1), b.mean(axis=1), y.mean(axis=1)
    ba, bb, by = SP.detail_bands(la), SP.detail_bands(lb), SP.detail_bands(ly)
    grain, err = [], []
    for k in range(BANDS):
        den = float((ba[k] * bb[k])[mask].sum())
        grain.append(float((by[k] * bb[k])[mask].sum()) / den if den > 0 else float('nan'))
        d = (ba[k] - bb[k])[mask].astype(np.float64)
        e = (by[k] - ba[k])[mask].astype(np.float64)
        dd = float((d * d).sum())
        err.append(1.0 + (4.0 * float((d * e).sum()) + 2.0 * float((e * e).sum())) / dd if dd > 0 else float('nan'))
    level = [float(y[:, c][mask].mean() / b[:, c][mask].mean()) for c in range(y.shape[1])]
    clip = float((ly[mask] >= CLIP_LEVEL).mean() - (lb[mask] >= CLIP_LEVEL).mean())
    got = ref = 0.0
    peaks = 0
    for t, (ys, xs, _) in enumerate(M.star_table(lb)):
        sel = mask[t][ys, xs]
        ys, xs = ys[sel], xs[sel]
        if not len(ys):
            continue
        peaks += len(ys)
        got += float((ly[t][ys, xs] - np.median(ly[t])).sum())
        ref += float((la[t][ys, xs] - np.median(la[t])).sum())
    return dict(grain=grain, err=err, level=level, clip=clip, stars=got / ref if ref > 0 else float('nan'),
                peaks=peaks, pixels=int(mask.sum()), mean_level=float(lb[mask].mean()))


def row(label, r):
    g = '/'.join(f'{v:5.3f}' for v in r['grain'])
    e = '/'.join(f'{v:5.3f}' for v in r['err'])
    lv = '/'.join(f'{v:5.3f}' for v in r['level'])
    return f"{label:16s} grain {g}   error left {e}   level {lv}   clip {100 * r['clip']:+6.2f} pt   stars {r['stars']:5.3f}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--cache', required=True)
    ap.add_argument('--bake', required=True, help='the store the cache was prepared from, for each master WCS')
    ap.add_argument('--models', nargs='+', required=True, help='slug=checkpoint.pt')
    ap.add_argument('--plane-truth-anchor', action='store_true', help="the orc condition, the scorer's anchor per session")
    a = ap.parse_args()

    dev = torch.device('cuda' if torch.cuda.is_available() else 'cpu')
    mm, meta = S.open_cache(a.cache)
    keys = meta['keys']
    val = [i for i in range(meta['train_cells'], meta['cells']) if meta['has_halves'][i]]
    session_of = {i: keys[i][0] for i in val}
    half_b = S.crop(np.asarray(mm[val, S.SLOT_HALF_B], dtype=np.float32))
    lb_all = half_b.mean(axis=1)
    peak_count = {i: len(p[0]) for i, p in zip(val, M.star_table(lb_all))}

    # CROWDED, fixed before any model runs: the Sgr cells with the most half-B peaks.
    sgr = [i for i in val if CROWDED_SESSION in session_of[i]]
    crowded = sorted(sgr, key=lambda i: -peak_count[i])[:CROWDED_CELLS]
    print(f"CROWDED: {len(crowded)} of {len(sgr)} Sgr Star Cloud cells, {peak_count[crowded[-1]]} to {peak_count[crowded[0]]} "
          f"half-B peaks each (the field's median cell {int(np.median([peak_count[i] for i in sgr]))})")

    # CORES: every cell whose cropped area holds a pixel within CORE_RADII half-light radii of a centre. A key is
    # (session, cell x, cell y) in the master's pixels; the cropped tile starts BORDER in from the cell's corner.
    size = half_b.shape[-1]
    border = S.BORDER
    print(f'a cache key reads {keys[val[0]]}')
    core_cells = {}
    for name, (ra, dec, r_arcmin, key) in CORES.items():
        sids = sorted({session_of[i] for i in val if key in session_of[i]})
        if not sids:
            print(f'{name}: its session is not in the cache')
            continue
        x, y, r = core_pixels(a.bake, sids[0], ra, dec, r_arcmin)
        reach = CORE_RADII * r
        held = []
        for i in val:
            if session_of[i] != sids[0]:
                continue
            ox, oy = keys[i][1] + border, keys[i][2] + border
            yy, xx = np.mgrid[0:size, 0:size]
            m = (xx + ox - x) ** 2 + (yy + oy - y) ** 2 <= reach ** 2
            if m.any():
                held.append((i, m))
        core_cells[name] = held
        print(f"{name}: centre ({x:.0f}, {y:.0f}) in its master, half-light radius {r:.1f} px, {len(held)} cells, "
              f"{sum(int(m.sum()) for _, m in held)} pixels within {CORE_RADII:g} radii")

    # Only the cells a population uses are denoised; the anchor reads every cell of each session.
    use = sorted(set(crowded) | {i for held in core_cells.values() for i, _ in held})
    pos = {i: k for k, i in enumerate(use)}
    half_a_u = np.asarray(mm[use, S.SLOT_HALF_A], dtype=np.float32)
    half_b_u = np.asarray(mm[use, S.SLOT_HALF_B], dtype=np.float32)
    raw, truth = S.crop(half_a_u), S.crop(half_b_u)
    sig, sig_has = S.open_sigma(a.cache, meta)
    planes = None
    if sig is not None and sig_has[use, S.SLOT_HALF_A].all():
        planes = np.asarray(sig[use, S.SLOT_HALF_A], dtype=np.float32)
        if a.plane_truth_anchor:
            half_a_all = S.crop(np.asarray(mm[val, S.SLOT_HALF_A], dtype=np.float32))
            master_all = S.crop(np.asarray(mm[val, S.SLOT_MASTER], dtype=np.float32))
            planes_all = S.crop(np.asarray(sig[val, S.SLOT_HALF_A], dtype=np.float32))
            la_all = half_a_all.mean(axis=1)
            stars_all = M.star_table(master_all.mean(axis=1))
            for sid in dict.fromkeys(session_of[i] for i in val):
                ts = [t for t, i in enumerate(val) if session_of[i] == sid]
                d = (la_all[ts] - lb_all[ts]) / np.sqrt(2.0)
                lvl = np.stack([gaussian_filter(lb_all[t], SP.DETAIL_LEVEL_SIGMA) for t in ts])
                keep = np.ones_like(lvl, dtype=bool)
                for j, t in enumerate(ts):
                    ys, xs, _ = stars_all[t]
                    for yy, xx in zip(ys.tolist(), xs.tolist()):
                        keep[j, max(0, yy - SP.DETAIL_MASK_PX):yy + SP.DETAIL_MASK_PX + 1,
                             max(0, xx - SP.DETAIL_MASK_PX):xx + SP.DETAIL_MASK_PX + 1] = False
                sky = (lvl >= 0.15) & (lvl < 0.30) & keep
                if sky.sum() < 3000:
                    print(f'plane truth anchor: {sid.split("|")[0][-44:]}: under 3000 sky pixels, left as estimated')
                    continue
                v = d[sky]
                truth_sigma = 1.4826 * float(np.median(np.abs(v - np.median(v))))
                factor = truth_sigma / (float(np.mean(planes_all[ts][sky])) / S.PLANE_SCALE)
                for i in use:
                    if session_of[i] == sid:
                        planes[pos[i]] *= factor
                print(f'plane truth anchor: {sid.split("|")[0][-44:]}: estimated x{factor:.3f}')

    populations = {'CROWDED': ([pos[i] for i in crowded], np.ones((len(crowded),) + raw.shape[-2:], dtype=bool))}
    for name, held in core_cells.items():
        if held:
            populations[name] = ([pos[i] for i, _ in held], np.stack([m for _, m in held]))

    outputs = {'input A': raw, 'gauss1 of A': np.stack([np.stack([gaussian_filter(ch, 1.0) for ch in t]) for t in raw])}
    for spec in a.models:
        slug, ckpt = spec.split('=', 1)
        path = ckpt if os.path.isabs(ckpt) else os.path.join(a.cache, ckpt)
        cond_map = bool(torch.load(path, map_location='cpu').get('cond_map', False))
        outputs[slug] = S.crop(S.denoise(a.cache, ckpt, half_a_u, dev, planes=planes if cond_map else None))

    condition = 'orc (plane truth anchor)' if a.plane_truth_anchor else 'est (planes as estimated)'
    print(f'\ncondition: {condition}; full strength; against half B; bands 0-1 / 1-2 px; level per channel R/G/B')
    for pop, (idx, m) in populations.items():
        r0 = measures(raw[idx], truth[idx], raw[idx], m)
        print(f"\n{pop}: {r0['pixels']:,} pixels, {r0['peaks']} half-B peaks, mean level {r0['mean_level']:.3f} (stretched)")
        # The scorer's own floor for a readable bin: a core the sampled cells barely reach is printed, not read.
        if r0['pixels'] < SP.DETAIL_MIN_PIXELS:
            print(f'  UNREADABLE: under the scorer\'s {SP.DETAIL_MIN_PIXELS} pixels; its rows are shown and are not a reading')
        for label, y in outputs.items():
            print(row(label, measures(raw[idx], truth[idx], y[idx], m)))


if __name__ == '__main__':
    main()
