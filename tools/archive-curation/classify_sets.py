"""#34: decide each capture set's frame type from the PIXELS and the SKY, and check the labels.

Input is stage 0's ledger (C:/temp/e2/stage0-gap.csv): 202 sets typed by IMAGETYP/FRAMETYP, 98 with
no type card at all. One decision per capture SET (folder + camera + exposure + gain + dims), taken
from up to three frames spread across it, because a set is one act at the telescope.

Two earlier runs of this script were wrong, and each failure is now a rule:

  1. astropy REFUSES to memory-map BZERO-scaled data (every unsigned 16-bit frame here). The read
     was wrapped in an except that wrote NaN, so every bias became REPORT. Reads are unguarded now:
     a level that cannot be read stops the run.
  2. The floor was "the lowest set median for this camera at this gain", and the flat test ran
     BEFORE the solve. Offsets change between sessions (SharpCap's BLKLEVEL), so an ASI294MC bias at
     offset 25 read 597x a floor of 3, and a 10 s Cen A light with a bright sky read 500x the
     ASI462's floor and was called FLAT without being solved. So: the floor is a BIAS (a set defined
     by its 0 s exposure, never by a label) of the same camera, gain and BLKLEVEL, preferring the one
     nearest in the folder tree; and the sky is asked before the level is.

Why not a star count at all: TianWen's own detector finds 141 "stars" at FWHM 1.7 px on a +4 C 120 s
ASI533 dark (hot pixels), and a 35 or 50 mm lens puts real stars at that width too. A blind solve
answers for a light and has nothing to match on a dark.

Stages, each cached so a verdict rule can change without re-reading the disk:
  levels   C:/temp/e2/cache-levels.json   median / MAD / p99.9 ADU of 3 frames per set
  solves   C:/temp/e2/cache-solves.csv    one blind solve per set, 30 s cap, keyed by frame path
  verdict  C:/temp/e2/sets-classified.csv rewritten whole on every run (pure function of the caches)

READ ONLY on the archive; solves write nothing (no --update-fits). --verdicts-only skips all I/O.
"""
import csv
import collections
import json
import os
import re
import subprocess
import sys
import time
import numpy as np
import astropy.io.fits as fits

GAP = r'C:/temp/e2/stage0-gap.csv'
LEVELS = r'C:/temp/e2/cache-levels.json'
SOLVES = r'C:/temp/e2/cache-solves.csv'
OUT = r'C:/temp/e2/sets-classified.csv'
TIANWEN = r'C:/Users/SebastianGodelet/source/repos/sharpastro/tianwen/src/TianWen.Cli/bin/Release/net10.0/tianwen.exe'
HEADERS = r'C:/temp/e2/cache-exptime.json'   # exact EXPTIME + CCD-TEMP per sampled frame
LEDGER = r'C:/temp/e2/archive-ledger.csv'     # OBJECT + DATE-OBS for the session key
SOLVE_TIMEOUT_S = 30
# A bias is the camera's MINIMUM exposure: 32 us on ZWO, 10 us on the Uranus-C, 1 us on QHY. The
# ledger rounded EXPTIME to two decimals, which made a 0.61 ms Moon frame a "0 s" bias, so the exact
# card is read (HEADERS) and the cut sits well above every minimum and well below any real capture.
BIAS_MAX_S = 0.0001
# Heat roughly doubles every 6 C. A dark whose excess per second, normalised to 25 C, is this many
# times its peers' (same camera, gain within 1 dB) did not see only heat. Measured on the ASI462MC at
# gain 80-85: seven real dark sets 0.7 to 6.2 ADU/s, the 2 s set at 86, sky lights 72 to 153.
LIT_DARK_FACTOR = 5.0
LIT_DARK_MIN_EXCESS_ADU = 50.0   # below this a short dark-flat's rate is only noise over a tiny exposure
MIN_LIGHTS_PER_SESSION = 10      # DatasetBuildOptions.MinSubsPerSession: the bake drops smaller sessions
DARK_MAX_RATIO = 1.5    # median / bias at or below this is dark-level (dark current included)
FLAT_MIN_RATIO = 3.0    # at or above this is flat-level

SOLVE_RE = re.compile(r'RA=([\d.]+)h Dec=([-\d.]+).*?scale=([\d.]+)')
verdicts_only = '--verdicts-only' in sys.argv


def set_key(r):
    return '|'.join((r['stage0'], os.path.dirname(r['path']), r['instrume'], r['exptime'], r['gain'],
                     r['naxis1'], r['naxis2']))


def level(path):
    with fits.open(path, memmap=False) as hdul:
        a = np.asarray(hdul[0].data[::4, ::4], dtype=np.float64)
    med = float(np.median(a))
    return [med, float(np.median(np.abs(a - med))), float(np.percentile(a, 99.9))]


def solve(path):
    t0 = time.time()
    try:
        p = subprocess.run([TIANWEN, 'solve', path], capture_output=True, timeout=SOLVE_TIMEOUT_S,
                           encoding='utf-8', errors='replace')
        m = SOLVE_RE.search(p.stdout)
        return (f'{m.group(1)}h {m.group(2)}d {m.group(3)}"/px' if m else 'no solution'), time.time() - t0
    except subprocess.TimeoutExpired:
        return f'timeout {SOLVE_TIMEOUT_S}s', time.time() - t0


rows = [r for r in csv.DictReader(open(GAP, encoding='utf-8')) if not r['stage0'].startswith('PRODUCT')]
sets = collections.defaultdict(list)
for r in rows:
    sets[set_key(r)].append(r)
for k in sets:
    sets[k].sort(key=lambda r: r['path'].lower())
print(f'{len(sets)} capture sets', flush=True)


def picks(k):
    rs = sets[k]
    return [rs[0], rs[len(rs) // 2], rs[-1]] if len(rs) >= 3 else rs


def middle(k):
    p = picks(k)
    return p[len(p) // 2]


# ---- stage 1: levels -------------------------------------------------------------------------
levels = json.load(open(LEVELS, encoding='utf-8')) if os.path.exists(LEVELS) else {}
if not verdicts_only:
    for i, k in enumerate(sorted(sets)):
        if k in levels:
            continue
        levels[k] = [level(r['path']) for r in picks(k)]
        if (i + 1) % 25 == 0:
            json.dump(levels, open(LEVELS, 'w', encoding='utf-8'))
            print(f'  levels: {i + 1}/{len(sets)}', flush=True)
    json.dump(levels, open(LEVELS, 'w', encoding='utf-8'))


headers = json.load(open(HEADERS, encoding='utf-8')) if os.path.exists(HEADERS) else {}


def exp_s(k):
    h = headers.get(middle(k)['path'])
    if h and h.get('exptime') is not None:
        return h['exptime']
    return float(sets[k][0]['exptime'] or 0)


def median_adu(k):
    return float(np.median([v[0] for v in levels[k]]))


# ---- the floor: a BIAS by exposure, same camera + gain + BLKLEVEL, nearest in the tree ----------
bias_sets = [k for k in sets if exp_s(k) <= BIAS_MAX_S and k in levels]


def floor_for(k):
    r0 = sets[k][0]
    cands = [b for b in bias_sets
             if b != k
             and sets[b][0]['instrume'] == r0['instrume'] and sets[b][0]['gain'] == r0['gain']
             and (not r0['blklevel'] or not sets[b][0]['blklevel'] or sets[b][0]['blklevel'] == r0['blklevel'])]
    if not cands:
        return None, ''
    d = os.path.dirname(r0['path']).lower()
    best = max(cands, key=lambda b: len(os.path.commonpath([d, os.path.dirname(sets[b][0]['path']).lower()])))
    return median_adu(best), os.path.relpath(os.path.dirname(sets[best][0]['path']), r'D:/Astro-Pics')


# ---- stage 2: solves -------------------------------------------------------------------------
solves = {}
if os.path.exists(SOLVES):
    for d in csv.DictReader(open(SOLVES, encoding='utf-8')):
        solves[d['path']] = (d['result'], float(d['seconds']))


def label(k):
    return sets[k][0]['type'] if k.startswith('TYPED') else ''


def needs_solve(k):
    if exp_s(k) <= BIAS_MAX_S:
        return False
    fl, _ = floor_for(k)
    # A set labelled FLAT that is clearly flat-level has nothing a solve could add, and a failed
    # blind solve is the expensive outcome. Everything else is asked: a narrowband light can sit on
    # the floor, and a bright-sky light far above it.
    return not (label(k) == 'FLAT' and fl and median_adu(k) / fl >= FLAT_MIN_RATIO)


if not verdicts_only:
    todo = [k for k in sorted(sets) if needs_solve(k) and middle(k)['path'] not in solves]
    print(f'  solves to run: {len(todo)}', flush=True)
    new_file = not os.path.exists(SOLVES)
    with open(SOLVES, 'a', newline='', encoding='utf-8') as fh:
        w = csv.writer(fh)
        if new_file:
            w.writerow(['path', 'result', 'seconds'])
        for i, k in enumerate(todo):
            p = middle(k)['path']
            result, secs = solve(p)
            solves[p] = (result, secs)
            w.writerow([p, result, f'{secs:.1f}'])
            fh.flush()
            if (i + 1) % 10 == 0:
                print(f'  solves: {i + 1}/{len(todo)}', flush=True)

# ---- stage 3: verdicts (pure) ----------------------------------------------------------------
def top_folder(path):
    return os.path.relpath(path, r'D:/Astro-Pics').split(os.sep)[0]


def flat_like(k):
    """Flat-level AND flat-shaped: a flat's p99.9 sits near its median, a Moon or a bright-sky light's
    does not. The shape is what keeps a lunar frame from reading as a flat."""
    fl, _ = floor_for(k)
    med = median_adu(k)
    p999 = float(np.median([v[2] for v in levels[k]]))
    return fl is not None and med / fl >= FLAT_MIN_RATIO and p999 <= 1.5 * med


def base_verdict(k):
    """Everything except the dark-versus-lit question, which needs the peers.

    A label is EVIDENCE, weighed against the sky and the level, never the answer: SharpCap's type is a
    dropdown, and 2024-02-03 holds 304 flats and 70 darks typed Light."""
    lab = label(k)
    fl, _ = floor_for(k)
    solved = solves.get(middle(k)['path'], ('', 0.0))[0]
    if exp_s(k) <= BIAS_MAX_S:
        return 'BIAS'
    if solved.endswith('/px'):
        return 'LIGHT'
    if not needs_solve(k):
        return 'FLAT'
    if not solved:
        return 'NOT-SOLVED-YET'
    if fl is None:
        # Nothing to measure the level against. A calibration label with nothing contradicting it
        # stands (the sky has already said it is not a light); a light that does not solve, or an
        # untyped set, needs a look.
        if lab in ('DARK', 'DARKFLAT', 'FLAT'):
            return lab if lab != 'DARKFLAT' else 'DARK'
        return 'UNSOLVED-LIGHT' if lab == 'LIGHT' else 'REPORT'
    ratio = median_adu(k) / fl
    if lab == 'FLAT':
        return 'FLAT' if ratio >= FLAT_MIN_RATIO else ('DARK' if ratio <= DARK_MAX_RATIO else 'REPORT')
    if lab == 'LIGHT':
        if ratio <= DARK_MAX_RATIO and 'dark' in os.path.relpath(os.path.dirname(sets[k][0]['path']), r'D:/Astro-Pics').lower():
            # On the floor, unsolved AND filed under a dark folder: a dark typed Light. The folder is
            # required because the level alone is not enough: 8 s narrowband through a small field
            # (2022 Saturn Nebula, 395 real lights) sits at 1.08x its bias and does not solve either.
            return 'DARK'
        if flat_like(k) and exp_s(k) <= 5.0:
            return 'FLAT'             # short, flat-level, flat-shaped: a flat typed Light
        return 'UNSOLVED-LIGHT'
    return 'DARK-CANDIDATE'


def rate_25c(k):
    """Excess over bias per second, normalised to 25 C by the usual doubling every 6 C."""
    fl, _ = floor_for(k)
    vals = []
    for r, v in zip(picks(k), levels[k]):
        h = headers.get(r['path']) or {}
        t, e = h.get('ccd_temp'), h.get('exptime')
        if t is not None and e:
            vals.append((v[0] - fl) / e * 2 ** ((25.0 - t) / 6.0))
    return float(np.median(vals)) if vals else None


def comparable_rate(k):
    """rate_25c brought to one gain, so darks at different gains on one camera are peers. ZWO gain is
    0.1 dB per unit (amplitude 10^(gain/200)); other vendors' units are not dB, so no conversion and
    only an exact gain is a peer. On the ASI462MC this puts the gain 100/230/307 heat darks at ~3x the
    gain 80 ones, a CONSISTENT offset (the model is not exact for that sensor), while the lit 2 s set
    is 30x: the factor is what separates them."""
    r = rates.get(k)
    if r is None:
        return None
    try:
        g = float(sets[k][0]['gain'])
    except ValueError:
        return None
    return r / 10 ** (g / 200.0) if sets[k][0]['instrume'].startswith('ZWO') else r


def peer_ok(p, k):
    a, b = sets[p][0], sets[k][0]
    return a['instrume'] == b['instrume'] and (a['instrume'].startswith('ZWO') or a['gain'] == b['gain'])


def in_dark_folder(k):
    return 'dark' in os.path.relpath(os.path.dirname(sets[k][0]['path']), r'D:/Astro-Pics').lower()


base = {k: base_verdict(k) for k in sets}
cands = [k for k in sets if base[k] == 'DARK-CANDIDATE']
rates = {k: rate_25c(k) for k in cands}

verdict, peer_rate = {}, {}
for k in sets:
    if base[k] != 'DARK-CANDIDATE':
        verdict[k] = base[k]
        continue
    peers = [p for p in cands if p != k and exp_s(p) >= 1.0 and comparable_rate(p) is not None and peer_ok(p, k)]
    fl, _ = floor_for(k)
    excess = median_adu(k) - fl
    mine = comparable_rate(k)
    if len(peers) >= 3 and mine is not None:
        med = float(np.median([comparable_rate(p) for p in peers]))
        peer_rate[k] = med
        # The rate comparison carries a gain model that is ~3x off on the ASI462MC, so it alone cannot
        # refuse a dark: the level must say so too. Both real leaks sit at 13-14x their bias; a dark
        # at 1.17x (2021 NGC 55 RGB session2) is warm, not lit.
        lit = (excess >= LIT_DARK_MIN_EXCESS_ADU and median_adu(k) / fl >= FLAT_MIN_RATIO
               and mine > LIT_DARK_FACTOR * max(med, 1e-3))
        if not lit:
            verdict[k] = 'DARK'
        elif label(k) in ('DARK', 'DARKFLAT') or in_dark_folder(k):
            verdict[k] = 'DARK-LIT'   # meant as a dark, and light reached it
        else:
            verdict[k] = 'UNSOLVED-LIGHT'   # sky-bright, not meant as a dark: a light the solver missed
    elif label(k) in ('DARK', 'DARKFLAT') or median_adu(k) / fl <= DARK_MAX_RATIO:
        verdict[k] = 'DARK'           # labelled or on the floor, and the sky has nothing: rate unchecked
    else:
        verdict[k] = 'REPORT'         # untyped, unsolved, above the floor, no peers to judge heat by

# The bake's session key is (night, camera, target, filter); filter is unknown on these, so this
# counts (camera, OBJECT or folder, UTC date), which on an Australian night is one date.
light_paths = {r['path'] for k in sets if verdict[k] in ('LIGHT', 'UNSOLVED-LIGHT') for r in sets[k]}
meta = {}
session_count = collections.Counter()
CAL_TYPES = ('BIAS', 'DARK', 'FLAT', 'OFFSET')
for r in csv.DictReader(open(LEDGER, encoding='utf-8')):
    if r['path'] in light_paths:
        meta[r['path']] = (r['object'].strip().lower(), r['date'])
    elif (r['verdict'] == 'SALVAGEABLE' and r['reachable'] == 'True'
          and not any(t in r['imagetyp'].upper() for t in CAL_TYPES)):
        # A session already partly filed: its filed frames count toward the floor, or the stragglers
        # of a filed night would be skipped as a "2-light session".
        obj = r['object'].strip().lower() or top_folder(r['path']).lower()
        session_count[(r['instrume'], obj, r['date'])] += 1
for k in sets:
    if verdict[k] in ('LIGHT', 'UNSOLVED-LIGHT'):
        for r in sets[k]:
            obj, date = meta.get(r['path'], ('', ''))
            session_count[(r['instrume'], obj or top_folder(r['path']).lower(), date)] += 1


# Lights that NO solve placed (blind 30 s, then a position + scale hint), judged by eye on their star
# counts from `tianwen image stats` (first / middle / last frame). The bake registers by star quads and
# never needs a solve, so the question is whether it has stars to work with. Recorded as judgements with
# their evidence, not as a threshold stretched from four sets.
UNSOLVED_JUDGED = {
    os.path.join('24mm LeHance 60s -10d', '2025-01-14', 'Light', '12_06_47Z', 'rawframes'):
        ('FILE', '79-154 stars at FWHM 2.1-2.35 px; 24 mm is too wide for the D50 index, not a bad frame'),
    os.path.join('Jellyfish Cluster', 'Light'):
        ('FILE', '44 stars at FWHM 4.4-5.4 px, SNR ~28: registrable; blind solve timed out at 180 s'),
    os.path.join('NGC 4945 RGB', '2021-02-24T23_09_08', 'rawframes'):
        ('SKIP', 'soft: 24-31 stars at FWHM 7.3-8.5 px (~25" at 3.4"/px), no solve'),
    os.path.join('Saturn Nebula', 'Light'):
        ('SKIP', '1-6 stars per frame: too few for the bake to register. Owner: a test through a 102 mm '
                 'f/10 Maksutov with no reducer (1020 mm, ~0.59 arcsec/px on the ASI290MM, a 19 x 11 arcmin field)'),
}


def filing(k):
    v = verdict[k]
    r0 = sets[k][0]
    rel = os.path.relpath(os.path.dirname(r0['path']), r'D:/Astro-Pics').lower()
    if 'simulator' in r0['instrume'].lower():
        return 'SKIP', 'a camera simulator, not the sky', ''
    if top_folder(r0['path']).lower() == 'focusing':
        return 'SKIP', 'focusing run', 'Focus'
    if rel.split(os.sep)[:2] == ['2022', 'moon'] or os.sep + 'moon' + os.sep in os.sep + rel + os.sep:
        return 'SKIP', 'the Moon, not deep sky', ''
    if v in ('LIGHT', 'UNSOLVED-LIGHT'):
        sessions = collections.Counter()
        for r in sets[k]:
            obj, date = meta.get(r['path'], ('', ''))
            sessions[(r['instrume'], obj or top_folder(r['path']).lower(), date)] += 1
        # The session holding most of this set's frames, not the smallest: one frame with a stray
        # OBJECT card must not skip the whole set.
        n = session_count[sessions.most_common(1)[0][0]]
        if n < MIN_LIGHTS_PER_SESSION:
            return 'SKIP', f'{n} lights in its session, under {MIN_LIGHTS_PER_SESSION}', 'Light'
        if v == 'UNSOLVED-LIGHT':
            d = os.path.dirname(r0['path'])
            judged = next((j for suffix, j in UNSOLVED_JUDGED.items() if d.endswith(suffix)), None)
            if judged:
                return judged[0], judged[1], 'Light'
            return 'LOOK', 'a light no solve placed and nobody has judged yet', 'Light'
        return 'FILE', '', 'Light'
    if v in ('BIAS', 'DARK', 'FLAT'):
        t = {'BIAS': 'Bias', 'DARK': 'DarkFlat' if label(k) == 'DARKFLAT' else 'Dark', 'FLAT': 'Flat'}[v]
        return 'IF-SERVES', 'calibration: filed only for a filed session', t
    reasons = {'DARK-LIT': 'a dark that caught light', 'UNSOLVED-LIGHT': 'labelled light, does not solve',
               'REPORT': 'unsolved and above the floor, no peers', 'NO-BIAS': 'no bias to judge the level by',
               'NOT-SOLVED-YET': 'solve still to run'}
    return 'SKIP', reasons.get(v, v), ''


counts = collections.Counter()
disagree = []
with open(OUT, 'w', newline='', encoding='utf-8') as fh:
    w = csv.writer(fh)
    w.writerow(['set_dir', 'instrume', 'exptime_s', 'gain', 'blklevel', 'naxis', 'frames', 'label', 'verdict',
                'agrees', 'file', 'file_reason', 'imagetyp', 'bias_adu', 'median_adu', 'ratio', 'rate_25c',
                'peer_rate_25c', 'solve', 'bias_from', 'sample'])
    for k in sorted(sets, key=lambda k: sets[k][0]['path'].lower()):
        r0 = sets[k][0]
        lab, v = label(k), verdict[k]
        fl, fl_from = floor_for(k)
        med = median_adu(k)
        ratio = med / fl if fl else float('nan')
        if not lab:
            agrees = ''
        elif v == lab or (lab == 'DARKFLAT' and v == 'DARK'):
            agrees = 'yes'
        elif v in ('NO-BIAS', 'NOT-SOLVED-YET', 'UNSOLVED-LIGHT'):
            agrees = '?'
        else:
            agrees = 'NO'
            disagree.append((os.path.relpath(os.path.dirname(r0['path']), r'D:/Astro-Pics'), lab, v, len(sets[k])))
        decision, reason, imagetyp = filing(k)
        counts[(decision, reason)] += len(sets[k])
        w.writerow([os.path.relpath(os.path.dirname(r0['path']), r'D:/Astro-Pics'), r0['instrume'], f'{exp_s(k):g}',
                    r0['gain'], r0['blklevel'], f"{r0['naxis1']}x{r0['naxis2']}", len(sets[k]), lab, v, agrees,
                    decision, reason, imagetyp, '' if fl is None else f'{fl:.0f}', f'{med:.0f}', f'{ratio:.2f}',
                    '' if rates.get(k) is None else f'{rates[k]:.1f}',
                    '' if k not in peer_rate else f'{peer_rate[k]:.1f}',
                    solves.get(middle(k)['path'], ('', 0.0))[0], fl_from, os.path.basename(middle(k)['path'])])

print('\nframes by filing decision:')
for (d, why), c in sorted(counts.items(), key=lambda kv: (kv[0][0], -kv[1])):
    print(f'  {d:9s} {c:6d}  {why}')
print(f'\nlabel disagreements ({len(disagree)} sets):')
for d, lab, v, n in disagree:
    print(f'  {lab:8s} -> {v:14s} {n:5d}  {d[-70:]}')
print('ledger:', OUT, flush=True)
