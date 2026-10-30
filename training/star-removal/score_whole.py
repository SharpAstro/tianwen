"""Round 2's scorecard: a star remover on WHOLE masters (docs/plans/star-remover-training.md, "Round 2"), one row per model.
Round 1 read our models on 256 px tiles; on whole masters the judges (StarNet2, SXT) were far ahead, and ours dig a hole in
blue's shoulder under Andromeda's bright stars and leave light in the Tarantula's sharp cores. This reads exactly that:
  - Andromeda (the owner's master): bright dark-sky stars flat and dug (remover_check2), and blue's residual at 1.5-3 and
    3-4.5 px over 2,160 bright isolated stars (profiles.py), in sigma;
  - the held-out Tarantula of 2026-10-17: bright dark-sky stars flat and left, the core residual R/G/B at 0-1.5 px, crowded
    stars flat, Gaia BP 17-19 removed and purity (remover_check2, gaia_remover_check, profiles);
  - every master: the glow left around bright clipped stars (glow_ring.py: median, p90 and the share above the config's
    glow_threshold, in the export's stretch and the master's noise sigma; Sagittarius is in score.json only);
  - the Sagittarius Triplet's crowded stars flat (mono, as three planes), and M31's nucleus taken (the guard, at most 5 %).
Plates are made at one view (how a model ships) into <out root>/<tag>/ and reused when present; a judge's existing
plates are linked in with --plates. Glue only: every number is parsed from the checks' own output.
    python score_whole.py <tag> --config scorecard.local.json --model a.onnx [b.onnx ...] [--views 1]
    python score_whole.py <tag> --config scorecard.local.json --plates andromeda=... tarantula=... sag=...
The machine's paths (the three masters, the tianwen executable, the output root) come from a config file, which
`scorecard.example.json` shows and `scorecard.local.json` (ignored by git) holds; each can be given on the command line
instead (--master NAME=PATH, --cli, --out-root). Nothing in this folder names a machine.
"""
import argparse
import json
import re
import shutil
import subprocess
import sys
from pathlib import Path

import numpy as np
from astropy.io import fits

HERE = Path(__file__).resolve().parent
NAMES = ('andromeda', 'tarantula', 'sag')
p = argparse.ArgumentParser()
p.add_argument('tag')
p.add_argument('--config', help='JSON with "cli", "out_root" and "masters" {andromeda, tarantula, sag}; '
                                'default scorecard.local.json beside this script when present')
p.add_argument('--cli', help='the tianwen executable (overrides the config)')
p.add_argument('--out-root', help='where <tag>/ is made (overrides the config)')
p.add_argument('--master', nargs='+', default=[], metavar='NAME=PATH', help='an input master (overrides the config)')
p.add_argument('--model', nargs='+')
p.add_argument('--views', default='1')
p.add_argument('--plates', nargs='+', default=[])
a = p.parse_args()
cfg_path = Path(a.config) if a.config else HERE / 'scorecard.local.json'
if a.config and not cfg_path.exists():
    raise SystemExit(f'config {cfg_path} does not exist')
cfg = json.loads(cfg_path.read_text(encoding='utf-8')) if cfg_path.exists() else {}
CLI = a.cli or cfg.get('cli')
OUT_ROOT = a.out_root or cfg.get('out_root')
MASTERS = {k: Path(v) for k, v in {**cfg.get('masters', {}), **dict(x.split('=', 1) for x in a.master)}.items()}
missing = [what for what, v in (('cli', CLI), ('out_root', OUT_ROOT), *((f'masters.{n}', MASTERS.get(n)) for n in NAMES)) if not v]
if missing:
    raise SystemExit(f'not set (config or command line): {", ".join(missing)}')
out = Path(OUT_ROOT) / a.tag
out.mkdir(parents=True, exist_ok=True)
log = open(out / 'score.log', 'a', encoding='utf-8')


def run(args, cwd=None):
    r = subprocess.run([str(x) for x in args], cwd=cwd or out, capture_output=True, text=True)
    log.write(f'$ {" ".join(map(str, args))}\n{r.stdout}\n{r.stderr}\n'); log.flush()
    if r.returncode != 0:
        raise SystemExit(f'{args[0]} {args[1] if len(args) > 1 else ""} exited {r.returncode}; see {out / "score.log"}')
    return r.stdout


plates = {}
given = dict(x.split('=', 1) for x in a.plates)
for k in NAMES:
    src = MASTERS[k]
    dst = out / f'{k}_starless.fits'
    if k in given and not dst.exists():
        shutil.copyfile(given[k], dst)
    if not dst.exists():
        if not a.model:
            raise SystemExit(f'no plate for {k}: give --model or --plates {k}=...')
        mono = ['--mono-as-rgb'] if k == 'sag' else []
        run([CLI, 'image', 'remove-stars', src, '-o', dst, '--no-stars', '--views', a.views, *mono, '--model', *a.model])
    plates[k] = dst


def three(path):
    """remover_check2 reads three channels: a mono plate goes in as three copies of its plane."""
    d = fits.getdata(path)
    if d.ndim == 3:
        return path
    t = out / f'rgb-{path.name}'
    if not t.exists():
        fits.PrimaryHDU(np.repeat(d[None], 3, 0)).writeto(t)
    return t


def check2_rows(text, name):
    r, cur = {}, None
    for line in text.splitlines():
        if re.match(r'^  [A-Za-z0-9_]+\s*$', line):
            cur = line.strip()
        elif cur == name:
            m = re.search(r'dark sky\s+SNR\s+100-1000.*?flat\s+([\d.]+) %, dug\s+([\d.]+) %, left\s+([\d.]+) %', line)
            if m:
                r['dark_flat'], r['dark_dug'], r['dark_left'] = map(float, m.groups())
            m = re.search(r'crowded \(3\+ within 15 px\)\s+n\s+\d+: flat\s+([\d.]+) %', line)
            if m:
                r['crowded_flat'] = float(m[1])
            m = re.search(r'BP 17-19:.*removed \(over half\) ([\d.]+) %', line)
            if m:
                r['bp1719'] = float(m[1])
            m = re.search(r'purity, whole frame: ([\d.]+) %', line)
            if m:
                r['purity'] = float(m[1])
    return r


def profile_rows(text, name):
    r = {}
    for line in text.splitlines():
        m = re.match(rf'^\s+{re.escape(name)} ([RGB]) residual:\s+(.*)$', line)
        if m:
            r[m[1]] = [float(x) for x in m[2].split()]
    return r


score = {}
k2 = HERE / 'remover_check2.py'
py = sys.executable
a_txt = run([py, '-I', k2, three(MASTERS['andromeda']), out / 'check2-andromeda', f'm={three(plates["andromeda"])}', '--cli', CLI])
score['andromeda'] = check2_rows(a_txt, 'm')
pa = run([py, HERE / 'profiles.py', MASTERS['andromeda'], f'm={plates["andromeda"]}'])
score['andromeda']['profile'] = profile_rows(pa, 'm')
t_txt = run([py, '-I', k2, MASTERS['tarantula'], out / 'check2-tarantula', f'm={plates["tarantula"]}', '--cli', CLI])
g_txt = run([py, '-I', HERE / 'gaia_remover_check.py', MASTERS['tarantula'], out / 'check2-tarantula', f'm={plates["tarantula"]}'])
score['tarantula'] = {**check2_rows(t_txt, 'm'), **check2_rows(g_txt, 'm')}
pt = run([py, HERE / 'profiles.py', MASTERS['tarantula'], f'm={plates["tarantula"]}'])
score['tarantula']['profile'] = profile_rows(pt, 'm')
s_txt = run([py, '-I', k2, three(MASTERS['sag']), out / 'check2-sag', f'm={three(plates["sag"])}', '--cli', CLI])
score['sag'] = check2_rows(s_txt, 'm')
glow_cfg = cfg.get('glow_threshold', {})
for k, master, plate in (('andromeda', MASTERS['andromeda'], plates['andromeda']), ('tarantula', MASTERS['tarantula'], plates['tarantula']),
                         ('sag', three(MASTERS['sag']), three(plates['sag']))):
    thr = ','.join(str(float(v)) for v in glow_cfg[k])      # a master with no threshold in the config is a KeyError, never a guess
    g_txt = run([py, '-I', HERE / 'glow_ring.py', master, plate, '--threshold', thr])
    score[k]['glow'] = {}
    for m in re.finditer(r'^glow ([RGB]) n (\d+) median ([-\d.]+) p90 ([-\d.]+) above ([\d.]+) %', g_txt, re.M):
        score[k]['glow'][m[1]] = {'n': int(m[2]), 'median': float(m[3]), 'p90': float(m[4]), 'above': float(m[5])}
    if sorted(score[k]['glow']) != ['B', 'G', 'R']:
        raise KeyError(f'glow_ring.py printed no glow line for every channel of {k}; see {out / "score.log"}')
nuc = run([py, HERE / 'm31_nucleus.py', MASTERS['andromeda'], plates['andromeda']]).strip().splitlines()[-1]
m = re.search(r'nucleus 0-6 px\s+([+-][\d.]+)% /\s+([+-][\d.]+)% /\s+([+-][\d.]+)%', nuc)
score['nucleus'] = [float(x) for x in m.groups()]
(out / 'score.json').write_text(json.dumps(score, indent=1))

def glow_field(d):
    """median / p90 / share of stars above the threshold, green (the luminance channel), in sigma-pixels and %."""
    g = d['glow']['G']
    return f"G {g['median']:.1f}/{g['p90']:.1f} above {g['above']:.0f}%"


A, T, S = score['andromeda'], score['tarantula'], score['sag']
row = (f"{a.tag:>10} | And dark flat {A['dark_flat']:5.1f} dug {A['dark_dug']:5.1f} | blue 1.5-3 {A['profile']['B'][1]:+.2f} "
       f"3-4.5 {A['profile']['B'][2]:+.2f} | Tar dark flat {T['dark_flat']:5.1f} left {T['dark_left']:5.1f} core "
       f"{T['profile']['R'][0]:+.2f}/{T['profile']['G'][0]:+.2f}/{T['profile']['B'][0]:+.2f} | Tar crowded {T['crowded_flat']:5.1f} "
       f"BP17-19 {T['bp1719']:5.1f} purity {T['purity']:.1f} | Sag crowded {S['crowded_flat']:5.1f} | nucleus "
       f"{'/'.join(f'{x:.1f}' for x in score['nucleus'])} % | And glow {glow_field(A)} | Tar glow {glow_field(T)}")
(out / 'row.txt').write_text(row + '\n')
print(row)
