"""Read E16b against its pre-registration (docs/plans/denoiser-training.md, "E16b, pre-registered" and its amendments).
READ-ONLY; run it only once run-e16b.ps1's status says done.

  python e16b_read.py            (the e16b-score-{orc,est}-* files in C:/temp/e2)

Predictions are read at full strength under orc, convmapb against convmap3, arm means over the seeds and then over the
fields a level is readable on for both arms; bands 0-1 / 1-2 / 2-4 px.
"""
import glob
import re
import statistics as st

LEVELS = ['<0.30', '0.30-0.45', '0.45-0.60', '>=0.60']
model_re = re.compile(r'^(\S+)\s+(\S+)\s*/\s*(\S+)\s*/\s*(\S+)\s+(\S+)\s*/\s*(\S+)\s*/\s*(\S+)\s+([-\d.]+)%:\s*(\S+)/\s*(\S+)/\s*(\S+)')
detail_re = re.compile(r'detail kept\s+(\S+(?: ref)?):\s*(.+)$')
error_re = re.compile(r'error left\s+(\S+(?: ref)?):\s*(.+)$')
ARMS = ['convmapb', 'convmap3', 'convmap', 'convrf', 'shipped']


def field_of(path):
    s = re.sub(r'^.*?e16b-score-(orc|est)-', '', path.replace('\\', '/'))[:-4]
    for pre in ('n2n-bb-eval4-', 'n2n-e2-eval4b-', 'n2n-eval4-', 'n2n-e16b-evalb-'):
        if s.startswith(pre):
            return s[len(pre):]
    return s


def cells(text):
    return [[None if v.strip() == '-' else float(v) for v in c.split(',')] for c in text.split('|')]


def parse(cond):
    d = dict(full={}, spend={}, matched={}, detail={}, err={})
    for path in sorted(glob.glob(f'C:/temp/e2/e16b-score-{cond}-*.txt')):
        fl = field_of(path)
        cur = None
        for line in open(path, encoding='utf-8', errors='replace'):
            s = line.strip()
            m = error_re.search(s)
            if m:
                label = m.group(1).strip()
                key = ('gauss1', 'full') if label.startswith('gauss1') else (cur, label)
                if key[0] is not None:
                    d['err'].setdefault(fl, {})[key] = cells(m.group(2))
                continue
            m = detail_re.search(s)
            if m:
                label, text = m.group(1).strip(), m.group(2)
                if 'never reaches' in text:
                    continue
                key = ('gauss1', 'full') if label.startswith('gauss1') else (cur, label)
                if key[0] is not None:
                    d['detail'].setdefault(fl, {})[key] = cells(text)
                continue
            mm = model_re.match(s)
            if mm:
                cur = mm.group(1)
                vals = [None if v in ('nan', '-') else float(v) for v in mm.group(2, 3, 4, 5, 6, 7)]
                d['full'].setdefault(cur, {})[fl] = float(mm.group(8))
                d['spend'].setdefault(cur, {})[fl] = tuple(float('nan') if x in ('nan', '-') else float(x) for x in mm.group(9, 10, 11))
                d['matched'].setdefault((fl, 4), {})[cur] = vals[0:3]
                d['matched'].setdefault((fl, 10), {})[cur] = vals[3:6]
    return d


arm = lambda n: n.split('_')[0]


def arm_value(table, fl, a, view, li, bi):
    """The arm's seed mean at one field, level and band, and the per-seed values; None where any seed is unreadable."""
    v = [vals[li][bi] for (n, vw), vals in table.get(fl, {}).items() if n is not None and arm(n) == a and vw == view]
    if not v or any(x is None for x in v):
        return None, v
    return st.mean(v), v


def over_fields(table, fields, arms, view, li, bi):
    """Per arm, the mean over the fields where every listed arm reads the bin; and those fields."""
    readable = [fl for fl in fields if all(arm_value(table, fl, a, view, li, bi)[0] is not None for a in arms)]
    return {a: (st.mean(arm_value(table, fl, a, view, li, bi)[0] for fl in readable) if readable else None) for a in arms}, readable


orc, est = parse('orc'), parse('est')
fields = sorted({fl for dd in orc['full'].values() for fl in dd})
print(f'{len(fields)} fields under orc: ' + ', '.join(fields))


def R(d, name):
    v = d['full'].get(name, {})
    return st.mean(v[fl] for fl in fields) if len(v) == len(fields) else None


def arm_R(d, a):
    v = [R(d, n) for n in d['full'] if arm(n) == a and R(d, n) is not None]
    return (st.mean(v), st.stdev(v) if len(v) > 1 else float('nan'), v) if v else (None, None, [])


print('\nR, the mean full-strength removal over the fields (percent), per arm (seed mean, sd, seeds):')
for cond, d in (('orc', orc), ('est', est)):
    for a in ARMS:
        m, sd, v = arm_R(d, a)
        if m is not None:
            print(f'  {cond} {a:9s} R {m:6.2f}  sd {sd:5.2f}  [' + ' '.join(f'{x:.2f}' for x in v) + ']')

E = orc['err']
print('\nERROR LEFT at full strength (orc), arm means over the fields both E16b arms read, bands 0-1 / 1-2 / 2-4:')
for li in range(4):
    parts, nf = [], None
    for a in ARMS:
        row = []
        for bi in range(3):
            means, readable = over_fields(E, fields, ['convmapb', 'convmap3'], 'full', li, bi)
            vals = [arm_value(E, fl, a, 'full', li, bi)[0] for fl in readable]
            vals = [x for x in vals if x is not None]
            row.append(f'{st.mean(vals):.3f}' if vals and len(vals) == len(readable) else '  -  ')
            if bi == 0:
                nf = len(readable)
        parts.append(f'{a} ' + '/'.join(row))
    print(f'  {LEVELS[li]:>9s} ({nf} fields)  ' + '   '.join(parts))

print('\n(1) and (2): the fall in the 0-1 px error left, convmap3 minus convmapb (a fall is positive); per field and the mean')
for li, label in ((2, '(1) 0.45-0.60'), (3, '(2) >=0.60')):
    means, readable = over_fields(E, fields, ['convmapb', 'convmap3'], 'full', li, 0)
    print(f'  {label}: {len(readable)} fields')
    falls = []
    for fl in readable:
        c, cs = arm_value(E, fl, 'convmap3', 'full', li, 0)
        b, bs = arm_value(E, fl, 'convmapb', 'full', li, 0)
        falls.append(c - b)
        print(f'    {fl[:60]:60s} control {c:.3f} [{" ".join(f"{x:.3f}" for x in cs)}]  arm {b:.3f} [{" ".join(f"{x:.3f}" for x in bs)}]  fall {c - b:+.3f}')
    if falls:
        f = st.mean(falls)
        verdict = 'HOLDS (at least 0.10)' if f >= 0.10 else ('INCONCLUSIVE at this dose (under 0.03)' if f < 0.03 else 'misses (0.03 to 0.10)')
        print(f'    mean fall {f:+.3f}: {verdict}')
        for bi in (1, 2):
            m2, r2 = over_fields(E, fields, ['convmapb', 'convmap3'], 'full', li, bi)
            if r2:
                print(f'    band {["0-1", "1-2", "2-4"][bi]} px over {len(r2)} fields: control {m2["convmap3"]:.3f}, arm {m2["convmapb"]:.3f}, fall {m2["convmap3"] - m2["convmapb"]:+.3f}')

D = orc['detail']
print('\n(3) detail kept at full strength, band 1-2 px (orc), arm means over the fields both arms read; KILL below 0.92:')
for li in (2, 3):
    means, readable = over_fields(D, fields, ['convmapb', 'convmap3'], 'full', li, 1)
    if readable:
        others = {a: over_fields(D, readable, [a], 'full', li, 1)[0][a] for a in ARMS}
        print(f'  {LEVELS[li]} over {len(readable)} fields: ' + '  '.join(f'{a} {v:.3f}' for a, v in others.items() if v is not None)
              + f"   -> {'holds (>= 0.97)' if means['convmapb'] >= 0.97 else 'KILL' if means['convmapb'] < 0.92 else 'misses (0.92 to 0.97)'}")
        for fl in readable:
            print(f'    {fl[:60]:60s} ' + '  '.join(f'{a} {arm_value(D, fl, a, "full", li, 1)[0]:.3f}' for a in ('convmapb', 'convmap3')))

print('\n(4) below 0.45: error left within 0.05 of the control in every band, and R within 4 points:')
ok = True
for li in (0, 1):
    for bi in range(3):
        means, readable = over_fields(E, fields, ['convmapb', 'convmap3'], 'full', li, bi)
        if not readable:
            continue
        diff = means['convmapb'] - means['convmap3']
        ok &= abs(diff) <= 0.05
        print(f'  {LEVELS[li]:>9s} band {["0-1", "1-2", "2-4"][bi]}: arm {means["convmapb"]:.3f} control {means["convmap3"]:.3f} diff {diff:+.3f}')
rb, rc = arm_R(orc, 'convmapb')[0], arm_R(orc, 'convmap3')[0]
print(f'  R arm {rb:.2f} control {rc:.2f} diff {rb - rc:+.2f}  -> {"holds" if ok and abs(rb - rc) <= 4 else "misses"}')


def arm_vals(d, fl, level, a):
    rows = d['matched'].get((fl, level), {})
    v = [rows[n] for n in rows if arm(n) == a]
    return None if not v or any(x is None for r in v for x in r[:2]) else v


print('\n(5) frontier, stars / compact spent at matched removal: the arm mean against the control seed range, per field')
worse = better = inside = unread = 0
for fl in fields:
    verdicts = []
    for level in (4, 10):
        a, b = arm_vals(orc, fl, level, 'convmapb'), arm_vals(orc, fl, level, 'convmap3')
        if a is None or b is None:
            continue
        ma = (st.mean(r[0] for r in a), st.mean(r[1] for r in a))
        lo = (min(r[0] for r in b), min(r[1] for r in b))
        hi = (max(r[0] for r in b), max(r[1] for r in b))
        tag = 'better' if ma[0] < lo[0] and ma[1] < lo[1] else 'worse' if ma[0] > hi[0] and ma[1] > hi[1] else 'inside'
        verdicts.append(f'{tag}@{level}% ({ma[0]:.1f}/{ma[1]:.1f} vs {lo[0]:.1f}..{hi[0]:.1f}/{lo[1]:.1f}..{hi[1]:.1f})')
    if not verdicts:
        unread += 1
        print(f'  {fl[:60]:60s} unreadable')
        continue
    if all(v.startswith('worse') for v in verdicts):
        worse += 1
    elif all(v.startswith('better') for v in verdicts):
        better += 1
    else:
        inside += 1
    print(f'  {fl[:60]:60s} ' + '  '.join(verdicts))
print(f'  => better {better}, worse {worse}, inside or mixed {inside}, unreadable {unread} of {len(fields)}'
      f"  -> {'holds' if worse <= 2 else 'misses'} (no worse on all but two)")

print('\n(6) est against orc for convmapb: full-strength removal, per field (est minus orc), and the mean:')
excess = {}
for fl in fields:
    o = [orc['full'][n][fl] for n in orc['full'] if arm(n) == 'convmapb' and fl in orc['full'][n]]
    e = [est['full'][n][fl] for n in est['full'] if arm(n) == 'convmapb' and fl in est['full'][n]]
    if o and e:
        excess[fl] = st.mean(e) - st.mean(o)
for fl, x in sorted(excess.items(), key=lambda kv: -kv[1]):
    print(f'  {fl[:60]:60s} {x:+6.2f}')
if excess:
    m = st.mean(excess.values())
    top = max(excess, key=excess.get)
    print(f'  mean {m:+.2f} points ({"within" if abs(m) <= 4 else "outside"} 4); largest on {top}')

print('\nTHE CONTROL AGAINST E16a (convmap3 against convmap, orc): error left within 0.05 everywhere, R within 4 points:')
worst = 0.0
for li in range(4):
    row = []
    for bi in range(3):
        means, readable = over_fields(E, fields, ['convmap3', 'convmap'], 'full', li, bi)
        if readable:
            diff = means['convmap3'] - means['convmap']
            worst = max(worst, abs(diff))
            row.append(f'{means["convmap3"]:.3f}/{means["convmap"]:.3f} ({diff:+.3f}, {len(readable)})')
        else:
            row.append('-')
    print(f'  {LEVELS[li]:>9s} ' + '   '.join(row))
r3, r0 = arm_R(orc, 'convmap3')[0], arm_R(orc, 'convmap')[0]
print(f'  R control {r3:.2f} E16a {r0:.2f} diff {r3 - r0:+.2f}; worst error-left difference {worst:.3f}'
      f"  -> {'holds' if worst <= 0.05 and abs(r3 - r0) <= 4 else 'misses'}")
