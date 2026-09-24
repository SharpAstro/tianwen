"""Audit, never delete: what would a "drop a path only if the inode keeps another" prune do?

The rule the user set is self-limiting and is the reason this is safe: a directory entry may be
removed only while `st_nlink > 1`, so the payload always remains reachable by at least one other
path. Applied repeatedly it converges to exactly one path per unique file and can never remove the
last one, whatever the state of any other tree.

What it does NOT do is free space. Unlinking one of several hard links returns no bytes; the extent
is released only when the final link goes, which this rule forbids. The win is walk cost and
duplicate ingestion, which is what was asked for.

This script only reports. Deletion is a separate, reviewed step the user runs: `prune_apply.py`.
"""
import csv
import os
import collections

PROV = "D:/Astro-Organized/_provenance"
CAND = os.path.join(PROV, "deletion-candidates.csv")
ORG = "D:/Astro-Organized"

rows = list(csv.DictReader(open(CAND, encoding="utf-8")))
print(f"deletion-candidates.csv: {len(rows):,} rows recorded by an earlier curation pass\n")

gone = present = 0
now = collections.Counter()
eligible, blocked, missing_rep, changed = [], [], [], 0

for r in rows:
    p = r["archive_path"]
    if not os.path.exists(p):
        gone += 1
        continue
    present += 1
    try:
        st = os.stat(p)
    except OSError:
        continue
    now[st.st_nlink] += 1
    rep = os.path.join(ORG, r["represented_by_rel"].replace("/", os.sep))
    rep_ok = os.path.exists(rep)
    if not rep_ok:
        missing_rep.append(p)
    if st.st_nlink > 1 and rep_ok:
        eligible.append((p, st.st_nlink, int(r["bytes"])))
    else:
        blocked.append((p, st.st_nlink, rep_ok))

print(f"  still on disk : {present:,}")
print(f"  already gone  : {gone:,}")
print(f"  link count NOW: {dict(sorted(now.items()))}\n")

print(f"ELIGIBLE (st_nlink > 1 AND its Organized copy exists): {len(eligible):,} paths, "
      f"{sum(b for _, _, b in eligible) / 2**30:.1f} GiB of payload (NOT reclaimed: other links hold it)")
print(f"BLOCKED  : {len(blocked):,}")
bn = collections.Counter()
for p, nl, rep_ok in blocked:
    bn["only link (nlink == 1)" if nl <= 1 else "Organized copy missing"] += 1
for k, v in bn.most_common():
    print(f"    {v:6,d}  {k}")
if missing_rep:
    print(f"\n  !! {len(missing_rep)} rows name an Organized copy that is NOT there, e.g.")
    for p in missing_rep[:3]:
        print(f"     {p}")

roots = collections.Counter()
for p, _, _ in eligible:
    q = p.replace("\\", "/")
    roots["/".join(q.split("/")[:3])] += 1
print("\neligible paths by location:")
for k, v in roots.most_common(8):
    print(f"  {v:6,d}  {k}")
