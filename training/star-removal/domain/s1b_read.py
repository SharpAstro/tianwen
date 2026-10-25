"""S1b's read (#1241, docs/plans/star-remover-training.md "Synthetics S1"): the small reports that turn the brightness-trend
export's files into the numbers F4's gate is read from. Every path is an argument.

  python s1b_read.py val-sessions --real <real root>
      the validation sessions (the real root's `master` frames), one a line
  python s1b_read.py shape-report --shapes <star-shapes jsonl> [--sessions <file>]
      per rig and channel the pooled log-gain slope and the masters and stars behind it
  python s1b_read.py wing-table --stats <domain_stats.json> [--bands 20,50,100,200,500]
      each source's star wing (3 to 5 px light over the peak, per channel, the median over the stars of a significance band) and
      the real over each synthetic, by band: the synthetics review's table
  python s1b_read.py clamp-report --injections <injections.jsonl> [...]
      the stars drawn, the stars the clamp moved and the ones it could not bring inside the envelope
  python s1b_read.py reads --real <real root> --synthetic NAME=ROOT [...] --out <dir> [--seeds 0,1,2,3,4] [--threads 6]
      the two instruments over every synthetic at once: domain_stats.py over all of them (stats.json), then one
      domain_classifier.py per synthetic (cls-NAME.json, its log NAME.log), run side by side, star and noise crops
"""
import argparse
import json
import os
import subprocess
import sys
from collections import defaultdict

import numpy as np

CHANNELS = 'RGB'


def _pick(d, name):
    """A JSON object's value by name, whatever the serialiser's casing."""
    for k, v in d.items():
        if k.lower() == name.lower():
            return v
    return None


def val_sessions(real_root):
    found = []
    with open(os.path.join(real_root, 'tiles-manifest.jsonl'), encoding='utf-8') as f:
        for line in f:
            r = json.loads(line)
            if r['Frame'] == 'master' and r['SessionId'] not in found:
                found.append(r['SessionId'])
    return found


def shape_rows(path):
    with open(path, encoding='utf-8') as f:
        return [json.loads(line) for line in f if line.strip()]


def shape_report(path, sessions_file):
    rows = shape_rows(path)
    keep = None
    if sessions_file:
        with open(sessions_file, encoding='utf-8') as f:
            keep = {l.strip() for l in f if l.strip()}
    by_rig = defaultdict(list)
    for r in rows:
        if keep is not None and _pick(r, 'SessionId') not in keep:
            continue
        by_rig[_pick(r, 'Rig') or '(no rig)'].append(r)
    print(f'{len(rows)} rows in {path}')
    for rig, group in sorted(by_rig.items()):
        slopes = []
        stars = np.zeros(3)
        readable = 0
        for r in group:
            s = _pick(r, 'LogSlopes')
            if s:
                slopes.append(s)
            bands = _pick(r, 'Bands') or []
            n = np.zeros(3)
            for b in bands:
                for c, ch in enumerate(_pick(b, 'Channels') or []):
                    n[c] += _pick(ch, 'Stars') or 0
            stars += n
            readable += int(n.sum() > 0)
        distinct = {tuple(round(x, 6) for x in s) for s in slopes}
        text = ' / '.join(f'{x:+.3f}' for x in sorted(distinct)[0]) if len(distinct) == 1 else \
            f'{len(distinct)} different slopes: ' + '; '.join(' / '.join(f'{x:+.3f}' for x in s) for s in sorted(distinct)[:4])
        print(f'{rig}: {len(group)} masters ({readable} with stars), slope R / G / B = {text}'
              f'   (stars per channel {int(stars[0])} / {int(stars[1])} / {int(stars[2])}; {len(slopes)} masters carry a slope)')
    return rows


def band_gains(path, sessions_file):
    """Per rig, the band gains as the median over the rig's masters, to set the slopes beside the review's ratio."""
    rows = shape_rows(path)
    keep = None
    if sessions_file:
        with open(sessions_file, encoding='utf-8') as f:
            keep = {l.strip() for l in f if l.strip()}
    out = defaultdict(lambda: defaultdict(list))
    for r in rows:
        if keep is not None and _pick(r, 'SessionId') not in keep:
            continue
        rig = _pick(r, 'Rig') or '(no rig)'
        for b in _pick(r, 'Bands') or []:
            lo = _pick(b, 'MinSignificance')
            for c, ch in enumerate(_pick(b, 'Channels') or []):
                if (_pick(ch, 'Weight') or 0) > 0:
                    out[rig][(lo, c)].append(_pick(ch, 'WingGain'))
    for rig, d in sorted(out.items()):
        print(f'{rig}: wing gain, median over masters with a readable band (masters), by band from significance:')
        for lo in sorted({k[0] for k in d}):
            cells = []
            for c in range(3):
                v = d.get((lo, c), [])
                cells.append(f'{np.median(v):.2f} ({len(v)})' if v else '-')
            print(f'   {lo:>6g}  R {cells[0]:>10}  G {cells[1]:>10}  B {cells[2]:>10}')


def wing_table(path, edges):
    data = json.load(open(path, encoding='utf-8'))
    bands = list(zip(edges, edges[1:] + [1e9]))
    table = {}
    for name, tiles in data.items():
        per = {b: [[], [], []] for b in bands}
        count = {b: 0 for b in bands}
        for t in tiles:
            for snr, wing in zip(t['star_snr'], t['star_wing']):
                for b in bands:
                    if b[0] <= snr < b[1]:
                        count[b] += 1
                        for c in range(3):
                            per[b][c].append(wing[c])
        table[name] = {b: ([float(np.median(per[b][c])) if per[b][c] else float('nan') for c in range(3)], count[b]) for b in bands}
    names = list(table)
    print('median 3-5 px light over the peak, R / G / B (stars), by significance band')
    for b in bands:
        label = f'{b[0]:g}-{b[1]:g}' if b[1] < 1e8 else f'{b[0]:g}+'
        cells = '   '.join(f'{n}: ' + ' / '.join(f'{x:.3f}' for x in table[n][b][0]) + f' ({table[n][b][1]})' for n in names)
        print(f'  {label:>9}: {cells}')
    if 'real' in table:
        print('real over each synthetic (mean of the three channels), by band')
        for n in names:
            if n == 'real':
                continue
            ratios = []
            for b in bands:
                r, s = np.array(table['real'][b][0]), np.array(table[n][b][0])
                ratios.append(f'{float(np.mean(r / s)):.2f}')
            print(f'  real / {n}: ' + ' / '.join(ratios) + '   at ' + ' / '.join(f'{b[0]:g}' for b in bands))


def clamp_report(paths):
    stars = clamped = unreached = rows = 0
    for p in paths:
        with open(p, encoding='utf-8') as f:
            for line in f:
                if not line.strip():
                    continue
                r = json.loads(line)
                rows += 1
                stars += len(_pick(r, 'Stars') or [])
                clamped += _pick(r, 'WingClamped') or 0
                unreached += _pick(r, 'WingUnreached') or 0
    print(f'{rows} injection rows, {stars} stars drawn, {clamped} with a channel moved to the envelope, {unreached} unreached (still outside)')


def reads(real, synthetics, out, seeds, threads):
    here = os.path.dirname(os.path.abspath(__file__))
    os.makedirs(out, exist_ok=True)
    stats = [sys.executable, '-I', os.path.join(here, 'domain_stats.py'), '--real', real, '--out', os.path.join(out, 'stats.json')]
    for spec in synthetics:
        stats += ['--synthetic', spec]
    with open(os.path.join(out, 'stats.log'), 'w', encoding='utf-8') as log:
        subprocess.run(stats, stdout=log, stderr=subprocess.STDOUT, check=True)
    procs = {}
    for spec in synthetics:
        name = spec.partition('=')[0]
        cmd = [sys.executable, '-I', os.path.join(here, 'domain_classifier.py'), '--real', real, '--synthetic', spec,
               '--out', os.path.join(out, f'cls-{name}.json'), '--variants', 'stars,noise', '--seeds', seeds, '--threads', str(threads)]
        procs[name] = subprocess.Popen(cmd, stdout=open(os.path.join(out, f'{name}.log'), 'w', encoding='utf-8'), stderr=subprocess.STDOUT)
    failed = [n for n, p in procs.items() if p.wait() != 0]
    for name in procs:
        with open(os.path.join(out, f'{name}.log'), encoding='utf-8') as f:
            for line in f:
                if 'AUC' in line:
                    print(line.rstrip())
    if failed:
        sys.exit(f'classifier failed: {", ".join(failed)}')


def main(argv):
    p = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    sub = p.add_subparsers(dest='cmd', required=True)
    v = sub.add_parser('val-sessions')
    v.add_argument('--real', required=True)
    s = sub.add_parser('shape-report')
    s.add_argument('--shapes', required=True)
    s.add_argument('--sessions')
    g = sub.add_parser('band-gains')
    g.add_argument('--shapes', required=True)
    g.add_argument('--sessions')
    w = sub.add_parser('wing-table')
    w.add_argument('--stats', required=True)
    w.add_argument('--bands', default='20,50,100,200,500')
    c = sub.add_parser('clamp-report')
    c.add_argument('--injections', nargs='+', required=True)
    r = sub.add_parser('reads')
    r.add_argument('--real', required=True)
    r.add_argument('--synthetic', action='append', required=True, metavar='NAME=ROOT[,FRAME]')
    r.add_argument('--out', required=True)
    r.add_argument('--seeds', default='0,1,2,3,4')
    r.add_argument('--threads', type=int, default=6)
    a = p.parse_args(argv)
    if a.cmd == 'val-sessions':
        print('\n'.join(val_sessions(a.real)))
    elif a.cmd == 'shape-report':
        shape_report(a.shapes, a.sessions)
    elif a.cmd == 'band-gains':
        band_gains(a.shapes, a.sessions)
    elif a.cmd == 'wing-table':
        wing_table(a.stats, [float(x) for x in a.bands.split(',')])
    elif a.cmd == 'reads':
        reads(a.real, a.synthetic, a.out, a.seeds, a.threads)
    else:
        clamp_report(a.injections)


if __name__ == '__main__':
    main(sys.argv[1:])
