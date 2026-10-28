"""A clipped star's halo: the azimuthal excess over the local sky, by radius, per rig, per channel (#1870).

Glue, pure numpy/scipy/astropy, every path an argument. Ported from DE1's glow_profile.py. Reads halo_census.py's census file.

For each clipped star, each channel's azimuthal MEDIAN at r = 0..40 px less the local sky (the median of the 45 to 60 px ring),
in two units: LINEAR (the master's unit scale, what an injector draws in; the stretch converts back, so fit linear and verify
stretched) and STRETCHED (the export's own input stretch, mirrored in halo_census.py, the master's own stretch as
`--stretch-from master` applies it). A synthetic tile is already stretched with its master's parameters, so its linear is the
exact inverse of that stretch (parameters from the store's tiles-manifest.jsonl). Stars are grouped by clipped-core radius
(1 px or less, 2, 3 to 5, 6 or more) and pooled by rig (INSTRUME), as `star-shapes --pool-by camera` pools; the rig "ALL" pools
every rig. Real masters are set "real"; the synthetic export is set "synth11" (the control), a seeded sample of about
--synth-sample clipped stars drawn per rig in proportion, at least --synth-floor of a rig's own where it has them.

    python -I halo_profile.py profile --census census.json --store <bake store> --out halo-profiles.json
        [--synth-sample 4000] [--synth-floor 150] [--seed 7] [--workers 4] [--note TEXT ...]
    python -I halo_profile.py table halo-profiles.json      # the stretched medians at 4, 6, 10, 20, 30 px, real beside synth
    python -I halo_profile.py check halo-profiles.json      # validates the schema, exits non-zero if it is wrong
"""
import argparse
import importlib.util
import json
import math
import sys
from concurrent.futures import ProcessPoolExecutor
from pathlib import Path

import numpy as np

_spec = importlib.util.spec_from_file_location('halo_census', Path(__file__).resolve().with_name('halo_census.py'))
census = importlib.util.module_from_spec(_spec)
sys.modules['halo_census'] = census
_spec.loader.exec_module(census)

RADII = 41
GROUPS = {'core1': ('core 1 px or less', lambda c: c <= 1),
          'core2': ('core 2 px', lambda c: c == 2),
          'core3-5': ('core 3 to 5 px', lambda c: 3 <= c <= 5),
          'core6+': ('core 6 px or more', lambda c: c >= 6)}
FALL_LEVELS = (0.03, 0.01, 0.003)
TABLE_RADII = (4, 6, 10, 20, 30)
MIN_STARS = 5
UNITS = ('linear', 'stretched')


def channel_names(n):
    return ['L'] if n == 1 else ['R', 'G', 'B'][:n]


def excess(planes, y, x):
    """(K, RADII) azimuthal median less the local ring's, per plane."""
    p = census.radial_medians(planes, y, x)
    return p[:, :RADII] - census.sky_level(p)[:, None]


def profile_master(args):
    """[(core, linear (C, RADII), stretched (C, RADII))] for the given stars of one master."""
    path, stars = args
    d, _ = census.load_master(path)
    absent = census.absent_mask(d)
    applied, mins, betas = census.stretch_parameters(d, absent)
    s = census.apply_stretch(d, mins, betas) if applied else d
    return Path(path).stem, [(c, excess(d, y, x), excess(s, y, x)) for y, x, c in stars]


def manifest_stretches(store):
    """session folder -> (mins, betas) or None where the export left the master unstretched."""
    out = {}
    for line in open(Path(store, 'tiles-manifest.jsonl'), encoding='utf-8'):
        if not line.strip():
            continue
        r = json.loads(line)
        if r.get('Frame') != 'master':
            continue
        f = census.session_folder(r['SessionId'])
        if f not in out:
            out[f] = (r['StretchOrigMin'], r['StretchBalance']) if r.get('StretchOrigMin') and r.get('StretchBalance') else None
    return out


def sample_synth(stars, total, floor, seed):
    """Per rig, a seeded sample sized in proportion to the whole, but no fewer than `floor` (or all) of a rig's own."""
    rng = np.random.default_rng(seed)
    by_rig = {}
    for i, s in enumerate(stars):
        by_rig.setdefault(s['rig'], []).append(i)
    picked = []
    for rig, idx in sorted(by_rig.items()):
        quota = min(len(idx), max(floor, round(total * len(idx) / max(1, len(stars)))))
        picked += [idx[j] for j in rng.choice(len(idx), quota, replace=False)]
    return [stars[i] for i in sorted(picked)]


def profile_synth(root, stars, stretches):
    """[(rig, core, linear, stretched)] for sampled synthetic stars; their tiles are stretched with their master's parameters."""
    by_tile = {}
    for s in stars:
        by_tile.setdefault(s['tile'], []).append(s)
    out = []
    for tile, ss in by_tile.items():
        t = np.fromfile(Path(root, tile), '<f2').astype(np.float32).reshape(-1, census.TILE, census.TILE)
        params = stretches.get(ss[0]['session'])
        lin = census.unstretch(t, params[0], params[1]) if params else t
        for s in ss:
            out.append((s['rig'], s['core'], excess(lin, s['y'], s['x']), excess(t, s['y'], s['x'])))
    return out


def rounded(v):
    return [None if not math.isfinite(x) else round(float(x), 7) for x in v]


def first_below(m, level):
    for r, v in enumerate(m):
        if v is not None and v < level:
            return r
    return RADII


def summarise(rows):
    """rows: [(core, linear (C, R), stretched (C, R))] of one set and rig -> {group: {stars, channels}}."""
    out = {}
    for g, (label, test) in GROUPS.items():
        sel = [r for r in rows if test(r[0])]
        if len(sel) < MIN_STARS:
            continue
        channels = {}
        for c, name in enumerate(channel_names(sel[0][1].shape[0])):
            ch = {}
            for u, k in (('linear', 1), ('stretched', 2)):
                a = np.stack([r[k][c] for r in sel if r[k].shape[0] > c])
                ch[u] = {'median': rounded(np.nanmedian(a, 0)),
                         'q25': rounded(np.nanpercentile(a, 25, 0)),
                         'q75': rounded(np.nanpercentile(a, 75, 0))}
            ch['stretched']['falls_under'] = {str(l): first_below(ch['stretched']['median'], l) for l in FALL_LEVELS}
            channels[name] = ch
        out[g] = {'label': label, 'stars': len(sel), 'channels': channels}
    return out


def cmd_profile(a):
    c = json.load(open(a.census, encoding='utf-8'))
    store_masters = {p.stem: p for p in Path(a.store, 'session-masters').glob('*.fits')}
    rows = {'real': {}, 'synth11': {}}
    jobs = []
    for m in c['masters']:
        if m['stars']:
            jobs.append((str(store_masters[m['session']]), [(s['y'], s['x'], s['core']) for s in m['stars']]))
    rig_of = c['rigs']
    n_masters = {}
    with ProcessPoolExecutor(max_workers=a.workers) as pool:
        for folder, res in pool.map(profile_master, jobs):
            rig = rig_of[folder]
            n_masters[rig] = n_masters.get(rig, 0) + 1
            rows['real'].setdefault(rig, []).extend(res)
            print(folder[:60], rig, len(res), flush=True)
    sampled = sample_synth(c['synth'], a.synth_sample, a.synth_floor, a.seed)
    print('synth sample', len(sampled), 'of', len(c['synth']), flush=True)
    for rig, core, lin, st in profile_synth(c['synth_root'], sampled, manifest_stretches(a.store)):
        rows['synth11'].setdefault(rig, []).append((core, lin, st))

    rigs = {}
    for kind, by_rig in rows.items():
        pooled = [r for rs in by_rig.values() for r in rs]
        for rig, rs in list(by_rig.items()) + [('ALL', pooled)]:
            groups = summarise(rs)
            if groups:
                rigs.setdefault(rig, {})[kind] = groups
    for rig, n in n_masters.items():
        rigs[rig]['masters'] = n
    rigs['ALL']['masters'] = sum(n_masters.values())
    out = {'schema': 1,
           'radii': list(range(RADII)),
           'units': {'linear': "the master's unit scale (divided by its maximum when above 1), excess over the 45-60 px ring",
                     'stretched': "ChunkedNafnetRunner.ApplyInputStretch's output (the master's own stretch), excess over the ring"},
           'groups': {g: label for g, (label, _) in GROUPS.items()},
           'min_stars': MIN_STARS,
           'seed': a.seed,
           'counts': {'real_clipped_read': sum(len(v) for v in rows['real'].values()),
                      'synth_clipped_in_census': len(c['synth']), 'synth_sampled': sum(len(v) for v in rows['synth11'].values()),
                      'synth_tiles': c['synth_tiles'], 'synth_injected': c['synth_injected']},
           'notes': list(a.note),
           'rigs': rigs}
    Path(a.out).parent.mkdir(parents=True, exist_ok=True)
    with open(a.out, 'w', encoding='utf-8') as fh:
        json.dump(out, fh, separators=(',', ':'))
    print('wrote', a.out, Path(a.out).stat().st_size, 'bytes')
    return 0


def cmd_table(a):
    j = json.load(open(a.json, encoding='utf-8'))
    for rig in ([a.rig] if a.rig else ['ALL']):
        print('== rig', rig, ' stretched median excess at r =', TABLE_RADII)
        for g, label in j['groups'].items():
            for kind in ('real', 'synth11'):
                grp = j['rigs'].get(rig, {}).get(kind, {}).get(g)
                if not grp:
                    continue
                for ch, v in grp['channels'].items():
                    m = v['stretched']['median']
                    print(f'  {g:8s} {kind:8s} {ch} n {grp["stars"]:6d} ' + ' '.join(f'{m[r]:.3f}' for r in TABLE_RADII)
                          + '  falls under ' + ' '.join(f'{l}:{p}' for l, p in v['stretched']['falls_under'].items()))
    return 0


def check(j):
    errs = []
    if not isinstance(j, dict) or j.get('schema') != 1:
        return ['schema is not 1']
    if j.get('radii') != list(range(RADII)):
        errs.append('radii is not 0..40')
    if not j.get('groups'):
        errs.append('no groups')
    rigs = j.get('rigs')
    if not isinstance(rigs, dict) or not rigs:
        return errs + ['no rigs']
    seen = {'real': 0, 'synth11': 0}
    for rig, body in rigs.items():
        for kind in ('real', 'synth11'):
            for g, grp in body.get(kind, {}).items():
                where = f'{rig}/{kind}/{g}'
                if g not in j.get('groups', {}):
                    errs.append(f'{where}: group not declared')
                if not isinstance(grp.get('stars'), int) or grp['stars'] <= 0:
                    errs.append(f'{where}: stars is not > 0')
                if not grp.get('channels'):
                    errs.append(f'{where}: no channels')
                for ch, v in grp.get('channels', {}).items():
                    for u in UNITS:
                        m = v.get(u, {}).get('median')
                        if not isinstance(m, list) or len(m) != RADII or any(x is None or not math.isfinite(x) for x in m):
                            errs.append(f'{where}/{ch}/{u}: median is not {RADII} finite numbers')
                seen[kind] += 1
    for kind, n in seen.items():
        if n == 0:
            errs.append(f'no {kind} group anywhere')
    if 'ALL' not in rigs:
        errs.append('no pooled rig ALL')
    for kind in ('real', 'synth11'):
        if not rigs.get('ALL', {}).get(kind):
            errs.append(f'ALL has no {kind} groups')
    return errs


def cmd_check(a):
    errs = check(json.load(open(a.json, encoding='utf-8')))
    for e in errs[:40]:
        print('FAIL', e)
    if errs:
        print(len(errs), 'problems')
        return 1
    print('ok')
    return 0


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest='cmd', required=True)
    p = sub.add_parser('profile')
    p.add_argument('--census', required=True)
    p.add_argument('--store', required=True)
    p.add_argument('--out', required=True)
    p.add_argument('--synth-sample', type=int, default=4000)
    p.add_argument('--synth-floor', type=int, default=150)
    p.add_argument('--seed', type=int, default=7)
    p.add_argument('--workers', type=int, default=4)
    p.add_argument('--note', action='append', default=[])
    p.set_defaults(fn=cmd_profile)
    t = sub.add_parser('table')
    t.add_argument('json')
    t.add_argument('--rig')
    t.set_defaults(fn=cmd_table)
    k = sub.add_parser('check')
    k.add_argument('json')
    k.set_defaults(fn=cmd_check)
    a = ap.parse_args()
    return a.fn(a)


if __name__ == '__main__':
    sys.exit(main())
