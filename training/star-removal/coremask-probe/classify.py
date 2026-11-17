"""Reads the probe's survey.jsonl (and, to say what changed, an earlier one) and reports the cores. Glue only: the rules
(edge reach, star bar) live in ExtendedCoreMask; this prints each kept core's own measures (EdgeDistancePx, PeakRatio) and
checks them against the bounds the probe recorded.

  python classify.py <new survey.jsonl> [<old survey.jsonl>]
"""
import json
import sys


def load(path):
    return [json.loads(line) for line in open(path, encoding="utf-8")]


def key(stem, c):
    return (stem, c["X"], c["Y"])


new = load(sys.argv[1])
cores = [(r["stem"], c) for r in new for c in r["cores"]]
print(f"{len(new)} masters, {sum(1 for r in new if r['cores'])} with a core, {len(cores)} cores kept")
if cores:
    reach = new[0]["edgeReachPx"]
    bar = new[0]["maxPeakRatio"]
    print(f"edge reach {reach} px, peak ratio bar {bar}")
    print("kept within edge reach:", [k for k in cores if k[1]["EdgeDistancePx"] < reach])
    print("kept over the star bar:", [k for k in cores if k[1]["peakRatio"] > bar])
    print("highest kept peak ratio:", max(c["peakRatio"] for _, c in cores))
    print("lowest kept edge distance:", min(c["EdgeDistancePx"] for _, c in cores))
if len(sys.argv) > 2:
    old = load(sys.argv[2])
    oldk = {key(r["stem"], c): c for r in old for c in r["cores"]}
    newk = {key(s, c): c for s, c in cores}
    gone = sorted(set(oldk) - set(newk))
    added = sorted(set(newk) - set(oldk))
    print(f"old {len(oldk)} cores; dropped {len(gone)}; kept {len(set(oldk) & set(newk))}; new {len(added)}")
    for g in gone:
        c = oldk[g]
        print("  dropped", g[0][:60], g[1], g[2], "peak", c["peak"], "core px", c["CoreAreaPx"])
    for a in added:
        print("  NEW", a)
