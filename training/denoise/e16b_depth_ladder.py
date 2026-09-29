"""Per session, the recorded per-frame noise estimates of subs, halves and master, against sqrt(N) depth scaling.

Reads the bake manifest (each row carries its frame's own estimated calibration) and each retained master's
stack count. If the estimator is honest and noise averages as sqrt(N), sub/sqrt(N/2) matches a half and
sub/sqrt(N) matches the master. D2 compares against the PAIR difference, which is a different measurement;
this separates "the estimator reads low" from "depth does not scale as sqrt(N)".
"""
import json, os, statistics, sys, re
from collections import defaultdict

bake = sys.argv[1]
wanted = sys.argv[2:]  # substrings of session ids

def stack_count(path):
    with open(path, 'rb') as f:
        while True:
            block = f.read(2880)
            if len(block) < 2880:
                return None
            for i in range(0, 2880, 80):
                card = block[i:i + 80].decode('ascii', 'replace')
                key = card[:8].strip()
                if key in ('STACKCNT', 'STACK_N', 'NCOMBINE', 'STACKED'):
                    m = re.match(r"\s*=\s*([0-9]+)", card[8:])
                    if m:
                        return key, int(m.group(1))
                if key == 'END':
                    return None

def sanitize(s):
    return re.sub(r'[<>:"/\\|?*]', '_', s)

frames = defaultdict(lambda: defaultdict(list))  # session -> frame -> [(sigma[], background[])]
with open(os.path.join(bake, 'tiles-manifest.jsonl'), encoding='utf-8') as f:
    for line in f:
        if wanted and not any(w in line for w in wanted):
            continue
        r = json.loads(line)
        sid = r['SessionId']
        if wanted and not any(w in sid for w in wanted):
            continue
        if r.get('NoiseSigma') is None:
            continue
        frames[sid][r['Frame']].append((r['NoiseSigma'], r['NoiseBackground']))

def med(rows, idx, c):
    return statistics.median(x[idx][c] for x in rows)

for sid in sorted(frames):
    fr = frames[sid]
    if not all(k in fr for k in ('master', 'halfmaster_a', 'halfmaster_b', 'sub')):
        continue
    mpath = os.path.join(bake, 'session-masters', sanitize(sid) + '.fits')
    sc = stack_count(mpath) if os.path.exists(mpath) else None
    n = sc[1] if sc else None
    ch = len(fr['master'][0][0])
    print(f"{sid}\n  N={n} ({sc[0] if sc else 'no card'})  rows: master {len(fr['master'])}, halves {len(fr['halfmaster_a'])}/{len(fr['halfmaster_b'])}, subs {len(fr['sub'])}")
    for c in range(ch):
        sm, bm = med(fr['master'], 0, c), med(fr['master'], 1, c)
        sa, ba = med(fr['halfmaster_a'], 0, c), med(fr['halfmaster_a'], 1, c)
        sb, bb = med(fr['halfmaster_b'], 0, c), med(fr['halfmaster_b'], 1, c)
        ss, bs = med(fr['sub'], 0, c), med(fr['sub'], 1, c)
        line = (f"  c{c}: sigma sub {ss:.3e} half {sa:.3e}/{sb:.3e} master {sm:.3e}; "
                f"background sub {bs:.4f} half {ba:.4f}/{bb:.4f} master {bm:.4f}; "
                f"half/master {((sa + sb) / 2) / sm:.3f} (sqrt2=1.414)")
        if n:
            line += (f"; sub/sqrt(N/2) / half {ss / (n / 2) ** 0.5 / ((sa + sb) / 2):.3f}"
                     f"; sub/sqrt(N) / master {ss / n ** 0.5 / sm:.3f}")
        print(line)
