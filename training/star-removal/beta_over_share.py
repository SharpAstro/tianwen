"""The share of an export's unsaturated, unlobed RGB stars drawn at a Moffat beta over a bound, per channel
(docs/plans/star-remover-training.md, R3w item 5: what the wing draw's high end was).

    python -I beta_over_share.py <injections.jsonl> [--over 6] [--every 40]

Reads one record in --every. Prints the share of stars whose red, green and blue Beta is over the bound, and the share
with at least one channel over it.
"""
import argparse
import json

p = argparse.ArgumentParser()
p.add_argument("injections")
p.add_argument("--over", type=float, default=6.0)
p.add_argument("--every", type=int, default=40)
a = p.parse_args()
n = 0
over = [0, 0, 0]
some = 0
records = 0
with open(a.injections, encoding="utf-8") as f:
    for i, line in enumerate(f):
        if i % a.every or not line.strip():
            continue
        records += 1
        for s in json.loads(line)["Stars"]:
            beta = s.get("Beta")
            if s.get("Saturated") or s.get("Lobe") or not beta or len(beta) != 3:
                continue
            n += 1
            hit = [b > a.over for b in beta]
            for c in range(3):
                over[c] += hit[c]
            some += any(hit)
print(f"{records} records, {n} stars; beta over {a.over:g}: red {100 * over[0] / n:.1f} %, green {100 * over[1] / n:.1f} %, "
      f"blue {100 * over[2] / n:.1f} %, at least one channel {100 * some / n:.1f} %")
