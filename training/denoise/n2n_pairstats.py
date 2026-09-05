"""Does a pair of tiles share NOISE, or only signal? The same statistic on both kinds of pair.

`tianwen dataset pair` records a residual correlation per cross-night pair (pairs.jsonl) and on the
real Rim Nebula pairs it read 0.07 to 0.23, not zero. That number cannot be read alone: two nights
share the SIGNAL, and whatever faint signal a 5x5 high-pass leaves in the faintest half of the scene
(sub-threshold stars, nebula texture) correlates the two sides whether or not any noise is shared. On
a field with 6,800 peaks per megapixel that is a lot of faint signal. The control is a pair that
shares signal AND noise: the same session's two half-masters, through the SAME code on the SAME cells'
worth of sky. What the cross-night pair reads BELOW the same-session pair is the shared noise the
cross-night pair escaped, and that difference is what H8 claims exists.

Reads tiles straight off a dataset root (a bake or a pair cache) through its tiles-manifest.jsonl, no
prepared cache needed: per session, every cell with a `halfmaster_a` and `halfmaster_b` tile. The
statistic per cell and channel, matching DatasetCrossNightExporter.ResidualStatistics:

    r = x - mean5x5(x) on the faintest half of the cell's luminance (by the `master` tile's channel 0),
    pixels beyond five MADs on either side dropped, Pearson correlation of r_a and r_b.

Reported per session as the median over cells, with the two sides' residual sigma (MAD-scaled, in the
tile's stretched units) so a level or gain mismatch between the sides shows up beside the correlation.
A per-tile `--sub` variant pairs the first two `sub` tiles instead, for the raw-sub regime.

Usage:
  python n2n_pairstats.py --root D:/Astro-Dataset/2026-09-full --session "Rim-Nebula/2026-02-18"
  python n2n_pairstats.py --root C:/temp/tianwen-scratch/n2n-pairs
"""
import argparse
import json
import os
from collections import defaultdict

import numpy as np
from scipy.ndimage import uniform_filter

TILE = 256
CH = 3
BORDER = 16                      # the metric's rim, dropped here too so the two sides agree with n2n_metrics


def read_tile(path):
    raw = np.fromfile(path, dtype='<f2')
    if raw.size != CH * TILE * TILE:
        raise SystemExit(f'{path}: {raw.size} values, expected {CH * TILE * TILE}')
    return raw.reshape(CH, TILE, TILE).astype(np.float32)


def residual(plane):
    """Pixel minus its 5x5 mean; NaN where the window is not finite."""
    finite = np.isfinite(plane)
    filled = np.where(finite, plane, 0.0)
    count = uniform_filter(finite.astype(np.float32), size=5, mode='constant')
    mean = uniform_filter(filled, size=5, mode='constant')
    ok = count >= 0.999
    out = np.full(plane.shape, np.nan, np.float32)
    out[ok] = plane[ok] - mean[ok]
    return out


def cell_statistics(a, b, master_lum):
    """Per channel: (sigma_a, sigma_b, correlation) over the faintest half of the cell."""
    a = a[:, BORDER:-BORDER, BORDER:-BORDER]
    b = b[:, BORDER:-BORDER, BORDER:-BORDER]
    # The faint half is judged on the SCENE (a 15x15 mean), never on the pixel: the master pixel is the
    # mean of the two sides, so selecting on it being low conditions on the two noises summing low and
    # anticorrelates them (independent synthetic noise read -0.23 through a pixel mask).
    scene = uniform_filter(master_lum, size=15, mode='nearest')[BORDER:-BORDER, BORDER:-BORDER]
    faint = scene <= np.nanmedian(scene)
    out = []
    for c in range(a.shape[0]):
        ra = residual(a[c])
        rb = residual(b[c])
        m = faint & np.isfinite(ra) & np.isfinite(rb)
        if m.sum() < 64:
            out.append((np.nan, np.nan, np.nan, np.nan, np.nan, np.nan))
            continue
        xa, xb = ra[m], rb[m]
        mad_a = np.median(np.abs(xa - np.median(xa)))
        mad_b = np.median(np.abs(xb - np.median(xb)))
        keep = (np.abs(xa) <= 5 * 1.4826 * mad_a) & (np.abs(xb) <= 5 * 1.4826 * mad_b)
        if keep.sum() < 64:
            out.append((np.nan, np.nan, np.nan, np.nan, np.nan, np.nan))
            continue
        corr = np.corrcoef(xa[keep], xb[keep])[0, 1]
        scale = 1.4826 / 0.9798
        sa, sb = mad_a * scale, mad_b * scale
        # Do the two sides share the SIGNAL? var(a - b) is sa^2 + sb^2 when only the noise differs,
        # LESS when the noise is shared (the covariance term), MORE when the signal itself differs:
        # residual registration, PSF, a level or colour structure the global gain did not remove.
        # Over the faint half with stars clipped, and over every pixel (where misregistered stars
        # dominate). N2N's optimum is E[b | a]; a signal that differs between the sides is not
        # predictable from a and pulls that optimum toward the input, which is what a run that will
        # not denoise looks like from the outside.
        d = (a[c] - b[c])
        faint_d = d[m]
        faint_d = faint_d[np.abs(faint_d - np.median(faint_d)) <= 5 * 1.4826 * np.median(np.abs(faint_d - np.median(faint_d)))]
        excess_faint = float(np.var(faint_d) / (sa * sa + sb * sb))
        all_d = d[np.isfinite(d)]
        excess_all = float(np.var(all_d) / (sa * sa + sb * sb))
        # How much of the faint difference is SMOOTH: the variance of its 15x15 mean over the variance
        # of the difference. White noise puts 1/225 there and a bilinearly resampled noise a few
        # percent; a level or gradient mismatch the flattener and the global gain left behind puts
        # tens of percent there. Pixel-scale disagreement (registration, PSF) does not.
        smooth = uniform_filter(np.where(np.isfinite(d), d, 0.0), size=15, mode='nearest')
        low_frac = float(np.var(smooth[m]) / max(np.var(d[m]), 1e-30))
        out.append((sa, sb, corr, excess_faint, excess_all, low_frac))
    return out


def load_pairs(root, want_subs, session_filter):
    """session -> [(tile_a, tile_b, master_tile)] from the manifest."""
    cells = defaultdict(lambda: {'a': None, 'b': None, 'master': None, 'subs': []})
    with open(os.path.join(root, 'tiles-manifest.jsonl'), encoding='utf-8') as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            d = json.loads(line)
            sid = d['SessionId']
            if session_filter and session_filter.lower() not in sid.lower():
                continue
            key = (sid, d['CellX'], d['CellY'])
            frame = d['Frame']
            path = os.path.join(root, d['Tile'].replace('/', os.sep))
            if frame == 'master':
                cells[key]['master'] = path
            elif frame == 'halfmaster_a':
                cells[key]['a'] = path
            elif frame == 'halfmaster_b':
                cells[key]['b'] = path
            elif frame == 'sub':
                cells[key]['subs'].append(path)
    per_session = defaultdict(list)
    for (sid, _, _), entry in cells.items():
        if entry['master'] is None:
            continue
        if want_subs:
            subs = sorted(entry['subs'])
            if len(subs) >= 2:
                per_session[sid].append((subs[0], subs[1], entry['master']))
        elif entry['a'] and entry['b']:
            per_session[sid].append((entry['a'], entry['b'], entry['master']))
    return per_session


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--root', required=True, help='a bake or a pair cache: holds tiles-manifest.jsonl and tiles/')
    ap.add_argument('--session', default=None, help='substring of the session (or pair) id; default every one')
    ap.add_argument('--sub', action='store_true', help='pair the first two sub tiles instead of the two halves')
    ap.add_argument('--max-cells', type=int, default=300)
    a = ap.parse_args()

    per_session = load_pairs(a.root, a.sub, a.session)
    if not per_session:
        raise SystemExit('no cell with both sides found')
    kind = 'sub pair' if a.sub else 'half pair'
    print(f'{kind}s under {a.root}\n')
    print(f"{'session':60s} {'cells':>5} {'sigma a (R/G/B)':>21} {'sigma b (R/G/B)':>21} {'correlation (R/G/B)':>21} "
          f"{'var(a-b)/(sa2+sb2) faint':>25} {'all pixels':>17} {'smooth share':>20}")
    for sid, entries in sorted(per_session.items()):
        stats = []
        for tile_a, tile_b, master in entries[:a.max_cells]:
            ta, tb, tm = read_tile(tile_a), read_tile(tile_b), read_tile(master)
            stats.append(cell_statistics(ta, tb, tm.mean(axis=0)))
        arr = np.array(stats)                        # cells x channels x 5
        med = np.nanmedian(arr, axis=0)
        print(f"{sid.split('|')[0][-60:]:60s} {len(stats):5d} "
              f"{'/'.join(f'{v:.1e}' for v in med[:, 0]):>21} "
              f"{'/'.join(f'{v:.1e}' for v in med[:, 1]):>21} "
              f"{'/'.join(f'{v:6.3f}' for v in med[:, 2]):>21} "
              f"{'/'.join(f'{v:6.2f}' for v in med[:, 3]):>25} "
              f"{'/'.join(f'{v:5.1f}' for v in med[:, 4]):>17} "
              f"{'/'.join(f'{100 * v:4.1f}%' for v in med[:, 5]):>20}")
    print('\nA same-session half pair shares signal AND noise; a cross-night pair shares signal only. The gap '
          'between the two correlations, on the same sky, is the shared noise the cross-night pair escaped.\n'
          'var(a-b)/(sa2+sb2) reads 1 when only the noise differs between the sides, under 1 when noise is '
          'shared, over 1 when the SIGNAL differs (registration, PSF, level structure): an N2N target that '
          'is not the input\'s own signal.')


if __name__ == '__main__':
    main()
