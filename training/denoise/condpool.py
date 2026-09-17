"""Where does every master in the pool sit on the conditioning plane, against what the arms trained on?

Part A: inside the E2 S-warped cache, per training session, the plane on the CLEAN master tile against
the plane on the injected input the model was trained to denoise. If the input's p5 is above the
master's median for every session, the model never saw its own deployment point during training, by
construction of the depth range, before any question of which fields are in the pool.

Part B: every session of the 2026-09-full bake, the plane on up to 60 master tiles and 60 half_a
tiles, with membership flags (E2 training / eval4b / eval4 / v19d's d8 training), sorted by the
master's median plane. The deployed input is the MASTER; the evals score on half_a (1.41x the noise).

Usage: python condpool.py  (from training/denoise, or with it on PYTHONPATH)
"""
import json
import os
import sys
from collections import defaultdict

import numpy as np
import n2n_smoke as S

SCRATCH = 'C:/temp/tianwen-scratch/'
BAKE = 'D:/Astro-Dataset/2026-09-full'
PER_SESSION = 60


def plane(t):
    lum = t.mean(axis=0).ravel().astype(np.float32)
    return float((np.median(lum) - np.percentile(lum, 25)) * 100.0)


def read_tile(path):
    raw = np.fromfile(path, dtype='<f2')
    return raw.reshape(3, 256, 256).astype(np.float32)


def keys_of(cache, split):
    mm, meta = S.open_cache(SCRATCH + cache)
    tc = meta['cells']; tr = meta['train_cells']
    rng = range(0, tr) if split == 'train' else range(tr, tc)
    return {meta['keys'][i][0] for i in rng}


def short(sid):
    d = sid.split('|')[0]
    return d[-34:]


def part_a():
    mm, meta = S.open_cache(SCRATCH + 'n2n-e2-warped')
    by = defaultdict(lambda: ([], []))
    for i in range(meta['train_cells']):
        sid = meta['keys'][i][0]
        by[sid][0].append(plane(np.asarray(mm[i, S.SLOT_MASTER], dtype=np.float32)))
        by[sid][1].append(plane(np.asarray(mm[i, 1], dtype=np.float32)))
    print('A. E2 S-warped training cache: clean master plane against the injected INPUT plane, per session')
    print(f"   {'session':34s} {'n':>3} {'master p5/med/p95':>22} {'input p5/med/p95':>22} {'input p5 / master med':>22}")
    for sid, (m, x) in sorted(by.items(), key=lambda kv: np.median(kv[1][0])):
        m, x = np.array(m), np.array(x)
        print(f"   {short(sid):34s} {len(m):3d} "
              f"{np.percentile(m,5):6.2f} {np.median(m):6.2f} {np.percentile(m,95):6.2f}   "
              f"{np.percentile(x,5):6.2f} {np.median(x):6.2f} {np.percentile(x,95):6.2f}   "
              f"{np.percentile(x,5)/np.median(m):8.2f}")
    print()


def part_b():
    flags = {
        'E2': keys_of('n2n-e2-warped', 'train'),
        'e4b': keys_of('n2n-e2-eval4b', 'val'),
        'e4': keys_of('n2n-eval4', 'val'),
        'd8': keys_of('n2n-d8', 'train'),
    }
    cells = defaultdict(lambda: {'master': [], 'half': []})
    with open(os.path.join(BAKE, 'tiles-manifest.jsonl'), encoding='utf-8') as fh:
        for line in fh:
            if not line.strip():
                continue
            d = json.loads(line)
            if d.get('Channels', 3) != 3:
                continue                      # mono stays out of every arm; its tiles are 1 x 256 x 256
            if d['Frame'] == 'master':
                cells[d['SessionId']]['master'].append(d['Tile'])
            elif d['Frame'] == 'halfmaster_a':
                cells[d['SessionId']]['half'].append(d['Tile'])
    rows = []
    for sid, e in cells.items():
        ms = sorted(e['master'])[:PER_SESSION]
        hs = sorted(e['half'])[:PER_SESSION]
        pm = np.array([plane(read_tile(os.path.join(BAKE, t))) for t in ms])
        ph = np.array([plane(read_tile(os.path.join(BAKE, t))) for t in hs]) if hs else np.array([np.nan])
        fl = ''.join(k if sid in v else '-' * len(k) for k, v in flags.items())
        rows.append((float(np.median(pm)), sid, len(pm), pm, ph, fl))
    rows.sort()
    unmatched = {k: len(v - set(cells)) for k, v in flags.items()}
    print(f'B. {BAKE}: {len(rows)} sessions, up to {PER_SESSION} master and half_a tiles each')
    print(f'   cache keys not found among the bake sessions (a key format mismatch, not a missing session): {unmatched}')
    print(f"   {'session':34s} {'n':>3} {'master p5/med/p95':>22} {'half_a med':>10}  flags (E2 train / eval4b / eval4 / d8 train)")
    for med, sid, n, pm, ph, fl in rows:
        print(f"   {short(sid):34s} {n:3d} {np.percentile(pm,5):6.2f} {med:6.2f} {np.percentile(pm,95):6.2f}   "
              f"{np.nanmedian(ph):8.2f}    {fl}")
    meds = np.array([r[0] for r in rows])
    e2_floor = 0.23
    print()
    print(f'   master median plane over the pool: min {meds.min():.2f}, p25 {np.percentile(meds,25):.2f}, '
          f'median {np.median(meds):.2f}, p75 {np.percentile(meds,75):.2f}, max {meds.max():.2f}')
    print(f'   sessions whose master median sits under the E2 injected floor ({e2_floor}): '
          f'{int((meds < e2_floor).sum())} of {len(meds)}')


if __name__ == '__main__':
    part_a()
    part_b()
