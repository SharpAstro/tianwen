"""Re-answer the twin question off digests.jsonl rather than by walking the disk.

The ledger keys on (dev, ino) and now covers five roots, so it can say where every link of a file
lives without reading anything. It also carries the DIGEST, which is strictly stronger than the
inode: it finds content that was COPIED as well as content that was linked, and a copy is just as
good an argument that a path is redundant.
"""
import csv
import json
import os
import collections

LEDGER = "D:/Astro-Reports/digests.jsonl"
CAND = "D:/Astro-Organized/_provenance/deletion-candidates.csv"


def root_of(p):
    q = p.replace("\\", "/")
    parts = q.split("/")
    return "/".join(parts[:2]) if len(parts) > 1 else q


recs = {}
for line in open(LEDGER, encoding="utf-8"):
    try:
        r = json.loads(line)
    except Exception:
        continue
    recs[r["path"].replace("\\", "/")] = r   # later record wins

by_ino = collections.defaultdict(list)
by_dig = collections.defaultdict(list)
for q, r in recs.items():
    by_ino[(r.get("dev"), r.get("ino"))].append(q)
    if r.get("digest"):
        by_dig[r["digest"]].append(q)

print(f"ledger: {len(recs):,} paths, {len(by_ino):,} inodes, {len(by_dig):,} distinct digests")
roots = collections.Counter(root_of(q) for q in recs)
for k, v in roots.most_common():
    print(f"  {v:8,d}  {k}")

rows = list(csv.DictReader(open(CAND, encoding="utf-8")))
link_twin, dig_twin, orphan = 0, 0, []
where = collections.Counter()
for r in rows:
    p = r["archive_path"].replace("\\", "/")
    rec = recs.get(p)
    if rec is None or not os.path.exists(p):
        continue
    if rec.get("nlink", 1) <= 1:
        continue
    others = [q for q in by_ino[(rec.get("dev"), rec.get("ino"))] if q != p]
    if others:
        link_twin += 1
        where[" + ".join(sorted({root_of(q) for q in others}))] += 1
        continue
    same = [q for q in by_dig.get(rec.get("digest"), []) if q != p]
    if same:
        dig_twin += 1
        where["(by DIGEST) " + " + ".join(sorted({root_of(q) for q in same}))] += 1
    else:
        orphan.append(p)

print(f"\neligible paths resolved by LINK: {link_twin:,}")
print(f"resolved only by DIGEST (a copy, not a link): {dig_twin:,}")
print(f"still unexplained: {len(orphan):,}")
print("\nwhere the survivor lives:")
for k, v in where.most_common(10):
    print(f"  {v:6,d}  {k}")
for p in orphan[:5]:
    print(f"  ORPHAN {p}")
