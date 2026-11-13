"""R3s's Q1 (docs/plans/star-remover-training.md, "R3s, pre-registered"): the share of an export's unsaturated, unlobed RGB stars
drawn with blue at RATIO times green's Moffat beta or more, and its per-session medians. Exits 0 inside [LOW, HIGH] percent,
1 outside, so a driver can stop before training.

    python -I channel_beta_share.py <injections.jsonl> [--ratio 2.4] [--low 3] [--high 5.5]
"""
import argparse
import json
import sys
from collections import defaultdict

import numpy as np

ap = argparse.ArgumentParser()
ap.add_argument("injections")
ap.add_argument("--ratio", type=float, default=2.4)
ap.add_argument("--low", type=float, default=3.0)
ap.add_argument("--high", type=float, default=5.5)
a = ap.parse_args()

stars, per = [], defaultdict(list)
for line in open(a.injections, encoding="utf-8"):
    if not line.strip():
        continue
    row = json.loads(line)
    for s in row["Stars"]:
        if s.get("Saturated") or s.get("Lobe") or len(s["Beta"]) < 3:
            continue
        q = s["Beta"][2] / s["Beta"][1]
        stars.append(q)
        per[row["SessionId"]].append(q)
q = np.array(stars)
share = 100.0 * float(np.mean(q >= a.ratio))
med = np.array([np.median(v) for v in per.values()])
print(f"{len(q)} unsaturated RGB stars over {len(per)} sessions: {share:.2f} % with blue at {a.ratio} times green's beta or more")
print(f"per star B/G beta p10/50/90/99 {np.percentile(q, [10, 50, 90, 99]).round(2)}; per-session medians p10/50/90 {np.percentile(med, [10, 50, 90]).round(2)}")
ok = a.low <= share <= a.high
print(f"Q1 {'holds' if ok else 'FAILS'}: bar {a.low} to {a.high} %")
sys.exit(0 if ok else 1)
