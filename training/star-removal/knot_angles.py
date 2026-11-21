"""Nebula knots an export drew with no angle (found 2026-11-20): an elongated knot laid along a steered orientation that is
not a number is drawn as a square of NaN, which the tile carries as zeros in the target and in every input draw.
    python -I knot_angles.py <export> [<export> ...]
Per export, over its cells (draw 0 of each): cells, cells holding a knot, knots, elongated knots, knots with a NaN
angle, cells holding one, and sessions holding one; then the sessions with the most."""
import json, os, sys
from collections import Counter

for exp in sys.argv[1:]:
    cells = with_knot = knots = elongated = nan = cells_nan = 0
    by = Counter(); sessions = set()
    for line in open(os.path.join(exp, "injections.jsonl"), encoding="utf-8"):
        r = json.loads(line)
        if r["Draw"] != 0:
            continue
        cells += 1; sessions.add(r["SessionId"])
        ks = r.get("Knots") or []
        with_knot += bool(ks); knots += len(ks)
        bad = 0
        for k in ks:
            elongated += k["SigmaMajorPx"] > 1.5 * k["SigmaMinorPx"]
            a = k.get("AngleDeg")
            if not isinstance(a, (int, float)) or a != a:
                bad += 1
        nan += bad
        if bad:
            cells_nan += 1; by[r["SessionId"]] += 1
    print(f"{exp}")
    print(f"   cells {cells} over {len(sessions)} sessions; with a knot {with_knot}; knots {knots}, elongated {elongated}; "
          f"with no angle {nan} ({100 * nan / max(elongated, 1):.1f} % of the elongated); cells holding one {cells_nan} "
          f"({100 * cells_nan / max(cells, 1):.2f} % of all cells), in {len(by)} sessions")
    for s, n in by.most_common(6):
        print(f"      {n:3d} cells  {s[:110]}")
