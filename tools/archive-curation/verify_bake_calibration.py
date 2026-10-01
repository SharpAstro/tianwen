"""Read-only, 2026-10-01: was every session of a bake calibrated with what the curation map assigns?

For each session the bake recorded (stats/psf-sessions.jsonl):
  used     the bake's recorded masters (Calibration) against what the resolver picks today
           (a `dataset coverage` TSV over the same roots), by name
  mapped   today's pick against _provenance/session-calibration-map.csv: the flat by its night (within a
           day of the mapped folder's), the dark by its night, gain, offset and exposure
A flip side is judged with its night. Prints only the sessions that fail, then a count.

  python verify_bake_calibration.py <store> <coverage.tsv>
"""
import csv
import json
import re
import sys
from datetime import date, datetime

store, coverage_tsv = sys.argv[1], sys.argv[2]
MAP = 'D:/Astro-Organized/_provenance/session-calibration-map.csv'

psf = {}
with open(f'{store}/stats/psf-sessions.jsonl', encoding='utf-8') as f:
    for line in f:
        if line.strip():
            r = json.loads(line)
            psf[r['SessionId']] = r
cov = {r['session_id']: r for r in csv.DictReader(open(coverage_tsv, encoding='utf-8-sig'), delimiter='\t')}
maprows = {(r['filter'], r['target'], r['night']): r for r in csv.DictReader(open(MAP, encoding='utf-8-sig'))}


def night_of(folder):
    m = re.search(r'(\d{4}-\d{2}-\d{2})', folder or '')
    return date.fromisoformat(m.group(1)) if m else None


def epoch(s):
    return datetime.fromisoformat(s[:19]).date() if s else None


def dark_spec(folder):
    """g<gain>-o<offset>-...-e<exp>s from a DARK/<night>-g252-o20-t-10-e120s folder name."""
    m = re.search(r'-g(\d+)-o(\d+).*-e([\d.]+)s', folder or '')
    return (int(m.group(1)), int(m.group(2)), float(m.group(3))) if m else None


bad = 0
for sid, rec in sorted(psf.items()):
    night_id = sid.split('|flip=')[0]
    parts = night_id.split('|')[0].split('/')
    key = (parts[1], parts[2], parts[3]) if len(parts) == 4 else None
    c = cov.get(night_id)
    cal = rec.get('Calibration') or {}
    problems = []
    if c is None:
        problems.append('no coverage row (not a session today)')
    else:
        if (cal.get('Flat') or '') != c['flat_slug']:
            problems.append(f"flat used {cal.get('Flat')!r}, today {c['flat_slug']!r}")
        if (cal.get('Dark') or '') != c['dark_slug']:
            problems.append(f"dark used {cal.get('Dark')!r}, today {c['dark_slug']!r}")
    m = maprows.get(key) if key else None
    if m is None:
        problems.append('no map row')
    elif c is not None:
        mf, ef = night_of(m['flat']), epoch(c['flat_epoch'])
        if m['flat'] and not (ef and mf and abs((ef - mf).days) <= 1):
            problems.append(f"flat {ef} is not the map's {m['flat']}")
        if not m['flat'] and c['flat_found'] == 'true':
            problems.append(f"the map has no flat, today picks {c['flat_slug']} ({ef})")
        md, ed = night_of(m['dark']), epoch(c['dark_epoch'])
        spec = dark_spec(m['dark'])
        if m['dark']:
            got = (int(c['dark_gain'] or -1), int(c['dark_offset'] or -1), float(c['dark_exposure_s'] or -1))
            if spec and got != spec:
                problems.append(f"dark {got} is not the map's {m['dark']}")
            elif md and ed and abs((ed - md).days) > 1:
                problems.append(f"dark {ed} is not the map's {m['dark']}")
        elif c['dark_found'] == 'true':
            problems.append(f"the map has no dark, today picks {c['dark_slug']}")
    if problems:
        bad += 1
        print(f"{sid[:110]}\n    " + '\n    '.join(problems))
print(f"\n{bad} of {len(psf)} baked sessions fail a check")
