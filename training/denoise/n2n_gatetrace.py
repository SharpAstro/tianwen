"""Per-step TRAJECTORIES from an n2n_smoke.py training log: what the gate session and the observer read
at chosen probe steps, one row per run, so a "trained toward the identity" or "fabricates from step
200" claim is a number rather than a phrase. n2n_gatelog.py is the per-seed SUMMARY (selected step,
minimum, final); this is the time axis it collapses.

Usage:
    python training/denoise/n2n_gatetrace.py C:/temp/e2/p2-star-b.log [--steps 200,400,1000,2000,3000,4000]
    python training/denoise/n2n_gatetrace.py C:/temp/e2/p2-star.log C:/temp/e2/p2-star-b.log

Reads the `gate` and `obs0` lines n2n_smoke.py prints at every probe (out/truth, stars kept as a
fraction of the truth's, stars@6 the low-bar count) and the `train arm X seed N` markers that start a
run (a log without arm markers is one arm, labelled by its file). Columns per chosen step: gate
out/truth and stars, observer out/truth and stars. Read a run left to right: a gate out/truth that
climbs back toward the input's ratio while the observer's stars fall is a net trading sharpening for
honesty; an observer star count that jumps by step 200 and never returns is the star term sharpening
every peak.
"""
import argparse
import os
import re
import sys
from collections import OrderedDict

RUN_MARKER = re.compile(r"^train (?:arm (\S+) )?seed (\d+)")
GATE_LINE = re.compile(r"^\s+gate\s+(\d+)\s+([\d.]+)\s+([\d.]+)\s+([+-]?[\d.]+)\s+([+-]?[\d.]+)\s+([\d.]+)\s+([\d.]+)")
OBS_LINE = re.compile(r"^\s+obs0\s+(\d+)\s+([\d.]+)\s+([\d.]+)\s+([+-]?[\d.]+)\s+([+-]?[\d.]+)\s+([\d.]+)\s+([\d.]+)")


def parse(path):
    runs = OrderedDict()
    label = os.path.splitext(os.path.basename(path))[0]
    key = None
    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            m = RUN_MARKER.match(line)
            if m:
                arm = m.group(1) or label
                key = (arm, int(m.group(2)))
                runs[key] = {"gate": {}, "obs": {}, "input": None, "obs_input": None}
                continue
            if key is None:
                continue
            g = GATE_LINE.match(line)
            if g:
                step = int(g.group(1))
                runs[key]["input"] = float(g.group(2))
                runs[key]["gate"][step] = (float(g.group(3)), float(g.group(6)), float(g.group(7)))
                continue
            o = OBS_LINE.match(line)
            if o:
                step = int(o.group(1))
                runs[key]["obs_input"] = float(o.group(2))
                runs[key]["obs"][step] = (float(o.group(3)), float(o.group(6)), float(o.group(7)))
    return runs


def nearest(table, step):
    if step in table:
        return table[step]
    below = [s for s in table if s <= step]
    return table[max(below)] if below else None


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("logs", nargs="+", help="one or more n2n_smoke.py training logs")
    p.add_argument("--steps", default="200,400,1000,2000,3000,4000",
                   help="probe steps to tabulate (a step with no probe reads the latest probe before it)")
    args = p.parse_args()
    steps = [int(s) for s in args.steps.split(",") if s.strip()]

    for path in args.logs:
        runs = parse(path)
        if not runs:
            print(f"{path}: no runs found", file=sys.stderr)
            continue
        first = next(iter(runs.values()))
        print(f"{path}: {len(runs)} run(s); gate input {first['input']}x truth, observer input {first['obs_input']}x")
        head = "| arm | seed | " + " | ".join(f"gate @{s} out/t, stars" for s in steps) + " | " + " | ".join(f"obs @{s} out/t, stars" for s in steps) + " |"
        print(head)
        print("|---|---:|" + "|".join("---" for _ in steps) + "|" + "|".join("---" for _ in steps) + "|")
        for (arm, seed), r in runs.items():
            gate_cells = []
            obs_cells = []
            for s in steps:
                g = nearest(r["gate"], s)
                gate_cells.append(f"{g[0]:.3f}, {g[1]:.2f}" if g else "-")
                o = nearest(r["obs"], s)
                obs_cells.append(f"{o[0]:.3f}, {o[1]:.1f}" if o else "-")
            print(f"| {arm} | {seed} | " + " | ".join(gate_cells) + " | " + " | ".join(obs_cells) + " |")
        print()


if __name__ == "__main__":
    main()
