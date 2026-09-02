r"""Three disjoint 8-session sets against the many-session arms, on the four observers.

Nothing trains. Each observer gets its own Gate (own raw-sub floor, own noise normalisation),
so arms are compared only within an observer, exactly as v20/v21/v23 did.

The question v23 left open: armD beats everything, armE does not, and armE was matched on camera
but carried a comet and a galaxy. armF is the third disjoint eight, built to armD's recipe and
screened for contamination neither earlier arm was checked for. Predictions PD / PE / PF are in
run-v24.ps1, written before the prepare.

Reading the SKULL column: armD contains a session from the same folder AND night as the Skull
observer (two mosaic panels of one night, named by different anchor objects). armE and armF have
no overlap with any observer. So that one cell is biased toward armD and the other three are not,
which is why the summary below reports the three clean observers separately.
"""
import io
import json
import sys

import numpy as np

import n2n_smoke as S
from n2n_bars import BIG, C21, D8, LEVELS, at, curve
from n2n_gate import Gate
from n2n_paths import cache

EVAL = cache("n2n-eval4")
E8 = cache("n2n-e8")
F8 = cache("n2n-f8")
CELLS = 48

ARMS = [("60 any", BIG, "n2n_v17c_s%d_final.pt"),
        ("21 any", C21, "n2n_v19c_s%d_final.pt"),
        ("8 D", D8, "n2n_v19d_s%d_final.pt"),
        ("8 E", E8, "n2n_v23e_s%d_final.pt"),
        ("8 F", F8, "n2n_v24f_s%d_final.pt")]
FEW = {"8 D", "8 E", "8 F"}
# The one observer whose cell is biased toward armD by a same-night sibling in its training set.
CONTAMINATED = "Skull and Crossbones"


def short(session):
    parts = session.split("|")
    return f"{parts[1].replace('ZWO ', '').replace('SVBONY ', '')} / {parts[2]}"[:44]


def main():
    import torch
    dev = "cuda" if torch.cuda.is_available() else "cpu"
    mm, meta = S.open_cache(EVAL)
    observers = S.observer_cells(meta, 0, CELLS)
    print(f"device {dev}, {len(observers)} observers, "
          f"{sum(len(c) for _s, c in observers)} cells total\n")

    models, seeds = {}, {}
    for label, cache, pattern in ARMS:
        got = []
        for i in range(3):
            try:
                models[(label, i)] = S.load_model(cache, pattern % i, dev)
                got.append(i)
            except FileNotFoundError:
                print(f"  {label} seed {i}: NO _final.pt, the gate never passed it")
        seeds[label] = got
    print()

    results = {}
    for session, cells in observers:
        gate = Gate(mm, cells, dev)
        print(f"=== {short(session)} ===", flush=True)
        print(f"    {len(cells)} cells, raw-sub floor {gate.floor_spurious:.1f} spurious/tile")
        for label, _cache, _pattern in ARMS:
            rows = [curve(gate, *models[(label, i)]) for i in seeds[label]]
            for lv in LEVELS[:2]:
                vals = [at(r, lv, "amp") for r in rows]
                got = [v for v in vals if v is not None]
                results[(session, label, lv)] = (float(np.mean(got)) if got else None, len(got))
            m, n = results[(session, label, 0.90)]
            print(f"      {label:8s} amp@0.90 {'  -  ' if m is None else f'{m:.3f}'}  "
                  f"({n}/{len(seeds[label])} usable seeds reach it)")
        print()

    for lv in LEVELS[:2]:
        print(f"=== faint amplitude kept at matched noise {lv:.2f}, per observer ===")
        print(f"{'observer':46s}" + "".join(f"{l:>12s}" for l, _c, _p in ARMS))
        for session, _cells in observers:
            mark = " *" if CONTAMINATED in session else "  "
            row = f"{short(session):44s}{mark}"
            for label, _c, _p in ARMS:
                m, n = results[(session, label, lv)]
                row += "       -    " if m is None else f"{m:9.3f}({n})"
            print(row)
        print(f"  * armD trained on a same-night sibling of this observer; read that column with "
              f"the bias in mind\n")

    # The decision summary PD/PE turn on: armF against armD and against the best many-session arm,
    # over the THREE uncontaminated observers only.
    clean = [s for s, _c in observers if CONTAMINATED not in s]
    print("=== armF verdict, over the three uncontaminated observers, at noise 0.90 ===")
    for label in ("8 D", "8 E", "8 F", "60 any"):
        vals = [results[(s, label, 0.90)][0] for s in clean]
        got = [v for v in vals if v is not None]
        shown = "  ".join("  -  " if v is None else f"{v:.3f}" for v in vals)
        print(f"  {label:8s} {shown}   mean {np.mean(got):.3f}" if got else f"  {label:8s} {shown}")
    for label in ("8 D", "8 E"):
        pairs = [(results[(s, "8 F", 0.90)][0], results[(s, label, 0.90)][0]) for s in clean]
        pairs = [(a, b) for a, b in pairs if a is not None and b is not None]
        if pairs:
            d = np.mean([a - b for a, b in pairs])
            print(f"  armF minus arm{label[-1]}: {d:+.3f} over {len(pairs)} clean observers")

    with io.open("../rotate-results.json", "w", encoding="utf-8") as f:
        json.dump({f"{s}|{l}|{lv}": v for (s, l, lv), v in results.items()}, f, indent=1)
    print("\nwritten ../rotate-results.json")
    return 0


if __name__ == "__main__":
    sys.exit(main())
