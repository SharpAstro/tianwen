"""Read the gate trajectories out of a training log and ask whether two configs sit on
DIFFERENT noise-vs-amplitude frontiers, or on the same one at different distances along it.

The distinction matters and a matched-step-count table cannot make it. Noise removed and faint
amplitude kept trade against each other along a single run's trajectory (every probe removes more
noise and keeps less amplitude than the one before it), so a config that trains more gently reads
as "keeps more amplitude, removes less noise" at step 4000 while being the SAME model family,
merely stopped earlier in effect. That is a different claim from "keeps more amplitude at equal
noise", which is the only version that would justify the regime.

So: interpolate each run's amplitude at a set of matched noise levels, then compare configs level
by level, always reporting the within-config spread beside the between-config gap.
"""
import re
import sys
from collections import defaultdict

import numpy as np

# "  gate    500     0.92x   0.96   1.00  +0.490   31.3      +5.5    0.924  FAIL"
RUN = re.compile(r"^=== (\S+) seed (\d+)")
GATE = re.compile(r"^\s+gate\s+(\d+)\s+([\d.]+)x\s+([\d.]+)\s+([\d.]+)\s+([+-][\d.]+)\s+"
                  r"([\d.]+)\s+([+-][\d.]+)")


def parse(path):
    runs, cur = {}, None
    for line in open(path, encoding="utf-8", errors="replace"):
        m = RUN.match(line)
        if m:
            cur = f"{m.group(1)}_s{m.group(2)}"
            runs[cur] = []
            continue
        m = GATE.match(line)
        if m and cur:
            runs[cur].append(dict(step=int(m.group(1)), noise=float(m.group(2)),
                                  amp=float(m.group(3)), detect=float(m.group(4)),
                                  resid=float(m.group(5)), spur=float(m.group(6)),
                                  over=float(m.group(7))))
    return runs


def metric_at(probes, level, key="amp"):
    """Interpolate a metric at a given noise level.

    A run's trajectory is not monotone in noise (it wanders), so this takes every consecutive
    pair of probes that BRACKETS the level and averages the interpolants, rather than assuming a
    single crossing. Returns None when the run never reaches the level, which is itself a finding
    and must not be silently filled in.
    """
    hits = []
    for a, b in zip(probes, probes[1:]):
        lo, hi = sorted((a, b), key=lambda p: p["noise"])
        if lo["noise"] <= level <= hi["noise"] and hi["noise"] > lo["noise"]:
            f = (level - lo["noise"]) / (hi["noise"] - lo["noise"])
            hits.append(lo[key] + f * (hi[key] - lo[key]))
    return float(np.mean(hits)) if hits else None


def spread(vals):
    v = [x for x in vals if x is not None]
    if len(v) < 2:
        return None
    return max(v) - min(v)


def fmt(x, w=5, p=3):
    return " " * w if x is None else f"{x:{w}.{p}f}"


def main(path):
    runs = parse(path)
    cfgs = defaultdict(list)
    for name, probes in runs.items():
        cfgs[name.split("_s")[0]].append((name, probes))
    for c in cfgs:
        cfgs[c].sort()

    print(f"{len(runs)} runs, {len(cfgs)} configs: " + ", ".join(
        f"{c} x{len(v)}" for c, v in sorted(cfgs.items())))

    print("\n=== at the final step (what a fixed step budget buys) ===")
    print(f"{'run':10s} {'noise':>6s} {'amp':>6s} {'detect':>7s} {'resid':>7s} {'over':>7s}")
    finals = defaultdict(lambda: defaultdict(list))
    for c, entries in sorted(cfgs.items()):
        for name, probes in entries:
            p = probes[-1]
            print(f"{name:10s} {p['noise']:6.2f} {p['amp']:6.2f} {p['detect']:7.2f} "
                  f"{p['resid']:+7.3f} {p['over']:+7.1f}")
            for k in ("noise", "amp", "detect", "resid", "over"):
                finals[c][k].append(p[k])
    print()
    for k in ("noise", "amp", "detect", "resid", "over"):
        cs = sorted(finals)
        line = f"  {k:7s}"
        for c in cs:
            v = finals[c][k]
            line += f"   {c}: {np.mean(v):+.3f} (spread {max(v) - min(v):.3f})"
        a, b = (finals[cs[0]][k], finals[cs[1]][k]) if len(cs) == 2 else (None, None)
        if a:
            gap = abs(np.mean(a) - np.mean(b))
            worst = max(max(a) - min(a), max(b) - min(b))
            overlap = not (max(a) < min(b) or max(b) < min(a))
            verdict = "OVERLAPS -> no effect" if overlap else f"separated (gap {gap:.3f})"
            line += f"   [{verdict}, worst within-config spread {worst:.3f}]"
        print(line)

    cs = sorted(cfgs)
    for key, label, width, prec in (("amp", "amplitude kept", 5, 3),
                                    ("over", "invented sources over the raw-sub floor", 6, 1),
                                    ("resid", "|residual correlation|", 6, 3)):
        print(f"\n=== {label} at MATCHED noise (does the frontier itself move?) ===")
        head = f"{'noise':>6s}"
        for c in cs:
            head += f" | {c:>28s}"
        print(head + " | verdict")
        for lv in np.arange(0.95, 0.59, -0.05):
            cells, per_cfg = [], {}
            for c in cs:
                vals = [metric_at(p, lv, key) for _, p in cfgs[c]]
                if key == "resid":
                    vals = [None if v is None else abs(v) for v in vals]
                per_cfg[c] = [v for v in vals if v is not None]
                cells.append(" ".join(fmt(v, width, prec) for v in vals)
                             + (f"  m={np.mean(per_cfg[c]):{width}.{prec}f}"
                                if per_cfg[c] else "  -" + " " * width))
            if len(cs) == 2 and all(len(per_cfg[c]) >= 2 for c in cs):
                a, b = per_cfg[cs[0]], per_cfg[cs[1]]
                if not (max(a) < min(b) or max(b) < min(a)):
                    v = "overlaps"
                else:
                    hi = cs[1] if np.mean(b) > np.mean(a) else cs[0]
                    v = f"{hi} higher by {abs(np.mean(a) - np.mean(b)):.{prec}f}"
            else:
                v = "too few runs reach this level"
            print(f"{lv:6.2f}" + "".join(f" | {c:>28s}" for c in cells) + f" | {v}")

    print("\nA level where one config has no runs means it never reached that noise level at all\n"
          "in 4000 steps, which is the 'stopped earlier in effect' reading, not a missing number.")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "train-v10.log")
