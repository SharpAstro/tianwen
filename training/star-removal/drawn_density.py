"""How crowded the drawn stars of a training export are, beside a real field (2026-11-23, the BP 17 to 19 gap).

    python -I drawn_density.py <export>/injections.jsonl [tiles to sample, default 300] [bytes to skip between tiles]

Reads the first lines of the injections list (one drawn tile a line) and prints, per tile: the stars drawn, and per
drawn star the count of other drawn stars within 16 px (the measure gaia_bp_by_level.py prints for a master's
catalogued stars). Reads only; it stops after the sample, so a multi-gigabyte list is not read through.
"""
import json
import sys

import numpy as np
from scipy.spatial import cKDTree

path = sys.argv[1]
want = int(sys.argv[2]) if len(sys.argv) > 2 else 300
skip = int(sys.argv[3]) if len(sys.argv) > 3 else 0
counts, crowd, keys = [], [], None
with open(path, "rb") as f:
    while len(counts) < want:
        if skip and counts:
            f.seek(skip, 1)
            f.readline()                      # to the next whole line
        line = f.readline()
        if not line:
            break
        rec = json.loads(line)
        if keys is None:
            keys = sorted(rec.keys())
            print("a line's keys:", keys)
        stars = next((v for v in rec.values() if isinstance(v, list) and v and isinstance(v[0], dict) and "X" in v[0]), [])
        counts.append(len(stars))
        if len(stars) >= 2:
            xy = np.array([(s["X"], s["Y"]) for s in stars])
            tree = cKDTree(xy)
            inside = (xy[:, 0] >= 16) & (xy[:, 0] < 240) & (xy[:, 1] >= 16) & (xy[:, 1] < 240)
            crowd.extend(len(tree.query_ball_point(p, 16.0)) - 1 for p in xy[inside])
counts, crowd = np.array(counts), np.array(crowd)
print(f"{len(counts)} drawn tiles: stars a tile median {np.median(counts):.0f}, 90th percentile {np.percentile(counts, 90):.0f}, "
      f"99th {np.percentile(counts, 99):.0f}, most {counts.max()}")
print(f"other drawn stars within 16 px of a drawn star ({len(crowd)} stars 16 px or more inside their tile): "
      f"median {np.median(crowd):.0f}, 90th percentile {np.percentile(crowd, 90):.0f}, 99th {np.percentile(crowd, 99):.0f}, "
      f"most {crowd.max()}; share with 38 or more {100 * np.mean(crowd >= 38):.2f} %, with 99 or more {100 * np.mean(crowd >= 99):.2f} %")
