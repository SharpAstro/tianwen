"""How crowded the drawn stars of a training export are, beside a real field (2026-11-23, the BP 17 to 19 gap).

    python -I drawn_density.py <export>/injections.jsonl [tiles, default 300] [bytes to skip between tiles, default 0]
                               [counts to report the share at or over, default 38,99]

Reads tiles of the injections list (one drawn tile a line) and prints the stars drawn a tile, and per drawn star the
count of other drawn stars within 16 px (the measure gaia_bp_by_level.py prints for a master's catalogued stars).
With no skip the tiles are the FIRST lines of the list, which is the first cells of the first sessions and no sample
of the export; give a skip (the list's size over the tiles wanted) to spread them through it. The default counts 38
and 99 are where the thirds of that measure are cut on the held-out Tarantula master (gaia_bp_by_level.py prints
them for the master it is given). Reads only, and never the whole of a multi-gigabyte list.
"""
import json
import sys

import numpy as np
from scipy.spatial import cKDTree

path = sys.argv[1]
want = int(sys.argv[2]) if len(sys.argv) > 2 else 300
skip = int(sys.argv[3]) if len(sys.argv) > 3 else 0
marks = [int(v) for v in (sys.argv[4] if len(sys.argv) > 4 else "38,99").split(",")]
counts, crowd = [], []
with open(path, "rb") as f:
    while len(counts) < want:
        if skip and counts:
            f.seek(skip, 1)
            f.readline()                      # to the next whole line
        line = f.readline()
        if not line:
            break
        stars = json.loads(line)["Stars"]
        counts.append(len(stars))
        if len(stars) >= 2:
            xy = np.array([(s["X"], s["Y"]) for s in stars])
            tree = cKDTree(xy)
            inside = (xy[:, 0] >= 16) & (xy[:, 0] < 240) & (xy[:, 1] >= 16) & (xy[:, 1] < 240)
            crowd.extend(len(tree.query_ball_point(p, 16.0)) - 1 for p in xy[inside])
counts, crowd = np.array(counts), np.array(crowd)
how = f"one every {skip:,} bytes of the list" if skip else "the FIRST lines of the list"
print(f"{len(counts)} drawn tiles ({how}): stars a tile median {np.median(counts):.0f}, "
      f"90th percentile {np.percentile(counts, 90):.0f}, 99th {np.percentile(counts, 99):.0f}, most {counts.max()}")
print(f"other drawn stars within 16 px of a drawn star ({len(crowd)} stars 16 px or more inside their tile): "
      f"median {np.median(crowd):.0f}, 90th percentile {np.percentile(crowd, 90):.0f}, "
      f"99th {np.percentile(crowd, 99):.0f}, most {crowd.max()}; "
      + ", ".join(f"share with {k} or more {100 * np.mean(crowd >= k):.2f} %" for k in marks))
