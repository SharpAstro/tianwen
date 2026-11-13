"""Saturated stars per drawn tile in an export against each master's own (the plan's R3q/R3r section, Q1b).

Real: the plate catalogue's subtracted saturated stars per 256x256 of frame area (the frame's NAXIS1 x NAXIS2; the canvas ring
is counted as area, so the real density reads a few per cent low). Drawn: per tile, the injected stars with Saturated true and
Lobe false whose centre lies INSIDE the 256 px tile (injections.jsonl, one pass). A cell is drawn with a margin about it, so
a row also lists stars centred outside the tile; counting them read R3r's export 13 % high and failed its Q1 for nothing.
Each row is parsed as JSON, so a session id the writer escapes (a '+' arrives as \\u002B) matches its line in sessions.txt;
a raw string match skipped two sessions. Prints per session and the pooled ratio drawn / real.
usage: python -I saturated_per_tile.py <injections.jsonl> <bake> <sessions.txt>
"""
import json, sys, os, statistics

TILE = 256

inj, bake, sessions_txt = sys.argv[1:4]
sessions = [s.strip() for s in open(sessions_txt, encoding='utf-8') if s.strip()]
plates = os.path.join(bake, 'starless', 'plates')

def slug(sid):
    return sid.replace('/', '_').replace('|', '_')

def naxis(path):
    with open(path, 'rb') as f:
        head = f.read(2880 * 4).decode('ascii', 'replace')
    cards = [head[i:i + 80] for i in range(0, len(head), 80)]
    v = {c[:8].strip(): c[10:30].strip() for c in cards}
    return int(v['NAXIS1']), int(v['NAXIS2'])

def real_density(sid):
    base = os.path.join(plates, slug(sid) + '_plate')
    w, h = naxis(base + '.fits')
    n = 0
    with open(base + '.stars.csv', encoding='utf-8') as f:
        hdr = f.readline().strip().split(',')
        io, isat = hdr.index('outcome'), hdr.index('saturated')
        for line in f:
            p = line.split(',')
            if p[io] == 'Subtracted' and p[isat] == '1':
                n += 1
    return n * 256 * 256 / (w * h), n

drawn = {}
with open(inj, encoding='utf-8') as f:
    for line in f:
        row = json.loads(line)
        c = drawn.setdefault(row['SessionId'], [0, 0])
        c[0] += 1
        c[1] += sum(1 for s in row['Stars']
                    if s['Saturated'] and not s['Lobe'] and 0 <= s['X'] < TILE and 0 <= s['Y'] < TILE)

rows = []
for sid in sessions:
    if sid not in drawn:
        continue
    d, n = real_density(sid)
    tiles, s = drawn[sid]
    rows.append((sid, d, n, s / tiles, tiles))
    print(f'{s / tiles:7.3f} drawn  {d:7.3f} real ({n:5d} sat)  ratio {(s / tiles) / d if d > 0 else float("nan"):6.2f}  {sid[:70]}')
real = sum(r[1] for r in rows) / len(rows)
dr = sum(r[3] for r in rows) / len(rows)
ratios = [r[3] / r[1] for r in rows if r[1] > 0.05]
print(f'sessions {len(rows)}  mean drawn/tile {dr:.3f}  mean real/tile {real:.3f}  pooled ratio {dr / real:.3f}  '
      f'median per-session ratio {statistics.median(ratios):.3f} over {len(ratios)} with real > 0.05/tile')
