"""Clipped stars in the model's own view, one census for the halo measurement (#1870).

Glue, pure numpy/scipy/astropy, every path an argument. Ported from DE1's star_census3.py.

The export's input stretch is mirrored from ChunkedNafnetRunner.ApplyInputStretch: the canvas ring (border-reachable pixels
that are zero or NaN in every channel) is left out of the measurement; the frame is left as it is when channel 0's median above
its covered minimum is already >= 0.125; otherwise each channel gets its covered minimum subtracted and the MTF that takes its
shifted median to 0.25 (a flat channel, median 0, is the identity). A master is first put on [0, 1] by DatasetTileExporter's
UnitDivisor (its maximum, when above 1), so a whole master is seen as the model sees it at inference.

A clipped star is a connected plateau of the stretched luminance (the channel mean) that is its 15 px neighbourhood's maximum
and >= 0.95, taken once at its centroid (star_census3 counted every pixel of a plateau). Its core is the radius out to which the
azimuthal median of the stretched luminance stays >= 0.95. A star whose 60 px window holds a canvas-ring pixel is skipped.

    python -I halo_census.py --store <bake store> --synth <synth export root> --out census.json
        [--test-sessions <file>] [--max-per-master 800] [--seed 7] [--workers 4]

The store holds session-masters/*.fits (one FITS per session, INSTRUME in its header) and tiles-manifest.jsonl; the synthetic root
holds injections.jsonl and its tiles. A master's rig is its INSTRUME card, which is what `tianwen dataset star-shapes
--pool-by camera` pools on. The census file holds one record per clipped star: its set (master or synth), session folder, rig,
position, core radius and stretched peak. halo_profile.py reads it.
"""
import argparse
import json
import sys
import warnings
from concurrent.futures import ProcessPoolExecutor
from pathlib import Path

import numpy as np
from astropy.io import fits
from scipy import ndimage

TILE = 256
CLIP = 0.95
TARGET_MEDIAN = 0.25
AUTO_DETECT_THRESHOLD = 0.125
RING = (45, 61)
WINDOW = 60
_YY, _XX = np.mgrid[-WINDOW:WINDOW + 1, -WINDOW:WINDOW + 1]
_R = np.hypot(_YY, _XX).round().astype(np.int32)


def session_folder(session_id):
    """The folder name the exporters give a session id ('a/b|c|d' becomes 'a_b_c_d')."""
    return session_id.split('|flip=')[0].replace('/', '_').replace('|', '_')


def read_test_sessions(path):
    """The session folders held out of training (test-sessions.txt: id, optional tab comment; '#' lines are notes)."""
    out = set()
    if path:
        for line in open(path, encoding='utf-8'):
            line = line.split('\t')[0].strip()
            if line and not line.startswith('#'):
                out.add(session_folder(line))
    return out


def master_files(store, test_folders):
    """Every training master of the store: session-masters/<folder>.fits, test sessions left out."""
    out = []
    for p in sorted(Path(store, 'session-masters').glob('*.fits')):
        if p.stem not in test_folders and not any(p.stem.startswith(t + '_flip') for t in test_folders):
            out.append(p)
    return out


def read_rig(path):
    return str(fits.getheader(path).get('INSTRUME', 'unknown')).strip() or 'unknown'


def load_master(path):
    """The master as (C, H, W) float32 on the unit scale DatasetTileExporter.ToUnitRange gives, and the divisor."""
    d = fits.getdata(path).astype(np.float32)
    d = d if d.ndim == 3 else d[None]
    peak = float(np.nanmax(d)) if d.size else 1.0
    divisor = peak if peak > 1.0 else 1.0
    if divisor != 1.0:
        d = d * np.float32(1.0 / divisor)
    return d, divisor


def absent_mask(d):
    """Border-reachable pixels that are zero or NaN in every channel: the canvas ring (Image.AbsentPixels)."""
    empty = np.all((d == 0) | np.isnan(d), axis=0)
    if not empty.any():
        return empty
    labels, _ = ndimage.label(empty)
    edge = np.unique(np.concatenate([labels[0], labels[-1], labels[:, 0], labels[:, -1]]))
    return np.isin(labels, edge[edge > 0])


def mtf(beta, x):
    return ((beta - 1.0) * x) / ((2.0 * beta - 1.0) * x - beta)


def stretch_parameters(d, absent):
    """(applied, per-channel covered minimum, per-channel balance), as ChunkedNafnetRunner.InputStretchParameters."""
    covered = ~absent
    mins, meds = [], []
    for c in range(d.shape[0]):
        v = d[c][covered]
        v = v[~np.isnan(v)]
        lo = float(v.min()) if v.size else 0.0
        mins.append(lo)
        meds.append(float(np.median(v - lo)) if v.size else 0.0)
    if meds[0] >= AUTO_DETECT_THRESHOLD:
        return False, None, None
    betas = [m * (TARGET_MEDIAN - 1.0) / (TARGET_MEDIAN * (2.0 * m - 1.0) - m) if m > 0 else 0.5 for m in meds]
    return True, mins, betas


def apply_stretch(d, mins, betas):
    out = np.empty_like(d)
    for c in range(d.shape[0]):
        out[c] = mtf(betas[c], np.clip(d[c] - mins[c], 0, None)).astype(np.float32)
    return out


def unstretch(s, mins, betas):
    """The exact inverse of apply_stretch (how a stretched synthetic tile goes back to the master's unit scale)."""
    out = np.empty_like(s)
    for c in range(s.shape[0]):
        b = betas[c]
        y = s[c].astype(np.float64)
        out[c] = (b * y / (y * (2.0 * b - 1.0) - (b - 1.0)) + mins[c]).astype(np.float32)
    return out


def radial_medians(planes, y, x, nr=WINDOW + 1):
    """(K, nr) medians of each plane over the integer-radius rings around (y, x), NaN skipped, NaN where a ring is empty.
    A window the frame cuts is read as far as it goes."""
    h, w = planes.shape[1:]
    y0, y1, x0, x1 = max(0, y - WINDOW), min(h, y + WINDOW + 1), max(0, x - WINDOW), min(w, x + WINDOW + 1)
    r = _R[y0 - y + WINDOW:y1 - y + WINDOW, x0 - x + WINDOW:x1 - x + WINDOW].ravel()
    order = np.argsort(r, kind='stable')
    sr = r[order]
    edges = np.searchsorted(sr, np.arange(nr + 1))
    vals = planes[:, y0:y1, x0:x1].reshape(planes.shape[0], -1)[:, order]
    out = np.full((planes.shape[0], nr), np.nan, np.float32)
    with warnings.catch_warnings():
        warnings.simplefilter('ignore', RuntimeWarning)
        for k in range(nr):
            if edges[k + 1] > edges[k]:
                out[:, k] = np.nanmedian(vals[:, edges[k]:edges[k + 1]], axis=1)
    return out


def core_radius(lum_profile):
    return int(np.argmax(~(lum_profile >= CLIP)))


def sky_level(profile):
    """The local sky: the median of the 45 to 60 px ring of a radial profile (last axis)."""
    with warnings.catch_warnings():
        warnings.simplefilter('ignore', RuntimeWarning)
        return np.nanmedian(profile[..., RING[0]:RING[1]], axis=-1)


def clipped_centroids(lum, margin):
    """One (y, x) per connected plateau of the stretched luminance that is its 15 px neighbourhood's maximum and >= CLIP."""
    h, w = lum.shape
    peaks = (lum == ndimage.maximum_filter(lum, size=15)) & (lum >= CLIP)
    labels, n = ndimage.label(peaks)
    if n == 0:
        return []
    cs = ndimage.center_of_mass(peaks, labels, range(1, n + 1))
    return [(int(round(cy)), int(round(cx))) for cy, cx in cs if margin <= cy < h - margin and margin <= cx < w - margin]


def census_master(args):
    """One master's clipped stars, capped at max_per (seeded by the master's position in the list)."""
    path, index, max_per, seed = args
    d, _ = load_master(path)
    absent = absent_mask(d)
    applied, mins, betas = stretch_parameters(d, absent)
    s = apply_stretch(d, mins, betas) if applied else d
    lum = s.mean(0)
    lum[absent] = np.nan
    hits = clipped_centroids(np.nan_to_num(lum, nan=0.0), 8)
    del d, s
    if len(hits) > max_per:
        keep = np.random.default_rng([seed, index]).choice(len(hits), max_per, replace=False)
        hits = [hits[i] for i in sorted(keep)]
    lum3 = lum[None]
    stars = []
    for y, x in hits:
        win = lum[max(0, y - WINDOW):y + WINDOW + 1, max(0, x - WINDOW):x + WINDOW + 1]
        if np.isnan(win).any():
            continue
        p = radial_medians(lum3, y, x)[0]
        stars.append({'y': y, 'x': x, 'core': core_radius(p), 'peak': round(float(lum[y, x]), 4)})
    return {'session': Path(path).stem, 'stretched': bool(applied), 'clipped_found': len(hits), 'stars': stars}


def census_synth(root, max_tiles, rig_of, seed):
    """Every injected star of the synthetic root's tiles whose stretched luminance clips at its position."""
    out, tiles, injected = [], 0, 0
    rng = np.random.default_rng(seed)
    lines = [l for l in open(Path(root, 'injections.jsonl'), encoding='utf-8') if l.strip()]
    if max_tiles and len(lines) > max_tiles:
        lines = [lines[i] for i in sorted(rng.choice(len(lines), max_tiles, replace=False))]
    for k, line in enumerate(lines):
        r = json.loads(line)
        tiles += 1
        injected += len(r['Stars'])
        raw = np.fromfile(Path(root, r['Tile']), '<f2').astype(np.float32)
        t = raw.reshape(-1, TILE, TILE)
        lum = t.mean(0)
        hits = [(int(round(st['Y'])), int(round(st['X']))) for st in r['Stars']]
        hits = [(y, x) for y, x in hits if 8 <= y < TILE - 8 and 8 <= x < TILE - 8 and lum[y, x] >= CLIP]
        folder = r['Tile'].split('/')[1]
        for y, x in hits:
            p = radial_medians(lum[None], y, x)[0]
            out.append({'y': y, 'x': x, 'core': core_radius(p), 'peak': round(float(lum[y, x]), 4),
                        'tile': r['Tile'], 'session': folder, 'rig': rig_of.get(folder, 'unknown')})
        if k % 2000 == 0:
            print('synth', k, 'tiles', len(out), 'clipped', flush=True)
    return out, tiles, injected


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--store', required=True, help='bake store: session-masters/, tiles-manifest.jsonl')
    ap.add_argument('--synth', required=True, help='synthetic export root: injections.jsonl and its tiles')
    ap.add_argument('--out', required=True, help='census JSON to write')
    ap.add_argument('--test-sessions', help='test-sessions.txt: those sessions are not training masters')
    ap.add_argument('--max-per-master', type=int, default=800, help='cap on clipped stars read per master (seeded)')
    ap.add_argument('--max-synth-tiles', type=int, default=0, help='read only this many synthetic tiles, seeded (0: all)')
    ap.add_argument('--seed', type=int, default=7)
    ap.add_argument('--workers', type=int, default=4)
    a = ap.parse_args()

    files = master_files(a.store, read_test_sessions(a.test_sessions))
    rig_of = {p.stem: read_rig(p) for p in files}
    print('masters', len(files), 'rigs', len(set(rig_of.values())), flush=True)
    result = {'schema': 1, 'store': str(a.store), 'synth_root': str(a.synth), 'seed': a.seed, 'rigs': rig_of,
              'masters': [], 'synth': [], 'synth_tiles': 0, 'synth_injected': 0}
    jobs = [(str(p), i, a.max_per_master, a.seed) for i, p in enumerate(files)]
    with ProcessPoolExecutor(max_workers=a.workers) as pool:
        for res in pool.map(census_master, jobs):
            res['rig'] = rig_of[res['session']]
            result['masters'].append(res)
            print(res['session'][:60], res['rig'], 'stretched', res['stretched'], 'clipped', res['clipped_found'],
                  'kept', len(res['stars']), flush=True)
    result['synth'], result['synth_tiles'], result['synth_injected'] = census_synth(a.synth, a.max_synth_tiles, rig_of, a.seed)
    print('synth clipped', len(result['synth']), 'of', result['synth_injected'], 'in', result['synth_tiles'], 'tiles', flush=True)
    with open(a.out, 'w', encoding='utf-8') as fh:
        json.dump(result, fh)
    return 0


if __name__ == '__main__':
    sys.exit(main())
