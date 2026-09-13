"""Read a training log's gate trajectories back as a table, one row per seed.

The pre-registered readouts of E2.7 and E2.8 are properties of the TRAJECTORY the gate printed
(where the width minimum sits and what the star count reads there, which step was selected and at
what width), and a log of 40 probes a seed is not something to read by eye twice without slipping
a row. This parses the `gate` / `obs<n>` lines `n2n_smoke.py --train` prints, splits them per
`train ... seed N` run, and prints per seed:

  selected   the step the gate selected and its out/truth, or `none` when no probe passed
  min        the trajectory's narrowest out/truth, its step, and the stars column THERE, which is
             E2.8's primary readout (the minimum is what the gate could not select in E2.7)
  @sel       stars and stars@6 at the selected step
  final      out/truth and stars at the last probe
  passes     how many probes passed the gate

`stars@6` (the report-only low-bar count) prints only where the log carries the column, which the
E2.7 logs do not; a dash there is "not recorded", not zero.

    python n2n_gatelog.py C:/temp/e2/p2-band.log --arm b
    python n2n_gatelog.py C:/temp/e2/p2-star.log
"""
import argparse
import math
import re

# The arm token is whatever the launcher wrote between `train` and `seed`: `arm C` on the E2 logs,
# `E3.1` on the operator's. Without the alternative the E3.1 log read as "no runs found".
RUN = re.compile(r"^train (?:(?:arm )?(\S+) )?seed (\d+) -> (\S+)")
GATE = re.compile(r"^\s+gate\s+(\d+)\s+([\d.]+)\s+([\d.nan]+)\s+([+\-\d.nan]+)\s+([+\-\d.nan]+)\s+([\d.nan]+)(?:\s+([\d.nan]+))?\s+([\d.inf]+)\s+(pass \*|pass|FAIL)")
OBS = re.compile(r"^\s+obs(\d)\s+(\d+)\s+([\d.]+)\s+([\d.nan]+)\s+([+\-\d.nan]+)\s+([+\-\d.nan]+)\s+([\d.nan]+)(?:\s+([\d.nan]+))?\s*$")
SELECTED = re.compile(r"^\s+selected step (\d+) of (\d+), score ([\d.]+)")
NOPASS = re.compile(r"^\s+NO probe passed every gate")
WEIGHT = re.compile(r"star-loss weight FIXED at ([\d.e+\-]+) on step (\d+)")


def f(x):
    try:
        return float(x)
    except (TypeError, ValueError):
        return float("nan")


def parse(path):
    runs = []
    run = None
    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            m = RUN.match(line)
            if m:
                run = {"arm": m.group(1), "seed": int(m.group(2)), "out": m.group(3), "gate": [], "obs": {}, "selected": None, "weight": None}
                runs.append(run)
                continue
            if run is None:
                continue
            m = GATE.match(line)
            if m:
                run["gate"].append({
                    "step": int(m.group(1)), "in": f(m.group(2)), "out": f(m.group(3)), "resid": f(m.group(4)),
                    "ring": f(m.group(5)), "stars": f(m.group(6)), "stars_lo": f(m.group(7)) if m.group(7) else None,
                    "score": f(m.group(8)), "mark": m.group(9)})
                continue
            m = OBS.match(line)
            if m:
                run["obs"].setdefault(int(m.group(1)), []).append({
                    "step": int(m.group(2)), "in": f(m.group(3)), "out": f(m.group(4)), "stars": f(m.group(7)),
                    "stars_lo": f(m.group(8)) if m.group(8) else None})
                continue
            m = SELECTED.match(line)
            if m:
                run["selected"] = (int(m.group(1)), float(m.group(3)))
                continue
            m = WEIGHT.search(line)
            if m:
                run["weight"] = (float(m.group(1)), int(m.group(2)))
    return runs


def fmt(v, spec=".3f"):
    return "-" if v is None or (isinstance(v, float) and math.isnan(v)) else format(v, spec)


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("log")
    p.add_argument("--arm", default=None, help="only runs of this arm letter (the E2.7 logs carry two)")
    p.add_argument("--stars-floor", type=float, default=0.60, help="the stars-at-minimum bar E2.8 predicts (>= 0.60)")
    p.add_argument("--width-bar", type=float, default=1.30, help="the selected out/truth bar E2.8 predicts (<= 1.30)")
    args = p.parse_args()

    runs = [r for r in parse(args.log) if args.arm is None or r["arm"] == args.arm]
    if not runs:
        raise SystemExit("no runs found")

    has_lo = any(g["stars_lo"] is not None for r in runs for g in r["gate"])
    print(f"{args.log}: {len(runs)} run(s)" + (f", arm {args.arm}" if args.arm else ""))
    # The observer is read at the SELECTED step (the checkpoint that would ship), falling back to the
    # width minimum when nothing was selected; E2.8b's pre-registration is stated at the selected step.
    hdr = (f"| seed | probes | passes | selected step | sel out/truth | stars @sel | {'stars@6 @sel | ' if has_lo else ''}"
           f"min out/truth | @step | stars @min | {'stars@6 @min | ' if has_lo else ''}final out/truth | final stars | "
           f"obs0 @sel out/truth | obs0 @sel stars | {'obs0 @sel stars@6 | ' if has_lo else ''}weight |")
    print(hdr)
    print("|" + "---|" * (hdr.count("|") - 1))
    n_sel = n_width = n_stars = 0
    for r in runs:
        g = r["gate"]
        if not g:
            print(f"| {r['seed']} | 0 | (no probes) |")
            continue
        passes = sum(1 for x in g if x["mark"].startswith("pass"))
        finite = [x for x in g if not math.isnan(x["out"])]
        mn = min(finite, key=lambda x: x["out"]) if finite else None
        sel = r["selected"]
        sel_row = next((x for x in g if sel and x["step"] == sel[0]), None)
        last = g[-1]
        obs0 = r["obs"].get(0, [])
        obs_step = sel[0] if sel else (mn["step"] if mn else None)
        obs_at_min = next((o for o in obs0 if obs_step is not None and o["step"] == obs_step), None)
        n_sel += sel is not None
        n_width += sel is not None and sel[1] <= args.width_bar
        n_stars += mn is not None and mn["stars"] >= args.stars_floor
        cells = [
            str(r["seed"]), str(len(g)), str(passes),
            str(sel[0]) if sel else "none", fmt(sel[1]) if sel else "-",
            fmt(sel_row["stars"], ".2f") if sel_row else "-",
        ]
        if has_lo:
            cells.append(fmt(sel_row["stars_lo"], ".2f") if sel_row else "-")
        cells += [fmt(mn["out"]) if mn else "-", str(mn["step"]) if mn else "-", fmt(mn["stars"], ".2f") if mn else "-"]
        if has_lo:
            cells.append(fmt(mn["stars_lo"], ".2f") if mn else "-")
        cells += [fmt(last["out"]), fmt(last["stars"], ".2f"),
                  fmt(obs_at_min["out"]) if obs_at_min else "-", fmt(obs_at_min["stars"], ".2f") if obs_at_min else "-"]
        if has_lo:
            cells.append(fmt(obs_at_min["stars_lo"], ".2f") if obs_at_min else "-")
        cells.append(f"{r['weight'][0]:.3e} @{r['weight'][1]}" if r["weight"] else "-")
        print("| " + " | ".join(cells) + " |")
    print()
    print(f"selected: {n_sel}/{len(runs)}; selected out/truth <= {args.width_bar}: {n_width}/{len(runs)}; "
          f"stars at the width minimum >= {args.stars_floor}: {n_stars}/{len(runs)}")
    ins = [g[0]["in"] for r in runs for g in [r["gate"]] if g]
    if ins:
        print(f"probed input sits at {min(ins):.3f} to {max(ins):.3f} x truth")


if __name__ == "__main__":
    main()
