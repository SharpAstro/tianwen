"""What is not yet in Astro-Organized, judged by CONTENT, off the ledger.

Built off digests.jsonl rather than a disk walk: a hand-written walk missed D:/Astro-Pics/Unsorted
entirely and undercounted the gap ninefold. The ledger cannot miss a folder, and it answers by
digest, which is what matters here because curation makes COPIES: a file already filed has the same
digest under Organized and a different inode, so any link-based check calls it missing.

Groups candidates by camera so they can be curated in batches that share a calibration story.
"""
import json
import os
import collections

import digest_ledger

ORG = "d:/astro-organized"

# Live paths only: a withdrawn Organized file's old record would otherwise count its content as filed.
recs = digest_ledger.load()

organized_digests = {r["digest"] for q, r in recs.items()
                     if q.lower().startswith(ORG) and r.get("digest")}
print(f"ledger {len(recs):,} paths; Organized holds {len(organized_digests):,} distinct digests\n")

# a candidate is anything under Pics or Unsorted whose content is not in Organized
sessions = collections.defaultdict(lambda: {"total": 0, "missing": 0, "bytes": 0})
for q, r in recs.items():
    ql = q.lower()
    if ql.startswith(ORG) or not r.get("digest"):
        continue
    if ql.startswith("d:/astro-pics/"):
        rest = q[len("D:/Astro-Pics/"):].split("/")
        key = "Pics:" + ("/".join(rest[:2]) if rest and rest[0][:4].isdigit() else rest[0])
    elif ql.startswith("d:/astro-unsorted/"):
        key = "Unsorted:" + q[len("D:/Astro-Unsorted/"):].split("/")[0]
    else:
        continue
    s = sessions[key]
    s["total"] += 1
    if r["digest"] not in organized_digests:
        s["missing"] += 1
        s["bytes"] += r.get("size", 0)

gaps = {k: v for k, v in sessions.items() if v["missing"]}
tot = sum(v["missing"] for v in gaps.values())
print(f"{len(sessions)} session trees under Pics/Unsorted; {len(gaps)} hold content not in Organized")
print(f"{tot:,} frames, {sum(v['bytes'] for v in gaps.values()) / 2**40:.2f} TiB\n")

print(f"{'missing':>8} {'of':>7} {'GiB':>7}   session")
for k, v in sorted(gaps.items(), key=lambda kv: -kv[1]["missing"])[:30]:
    full = "  <-- none filed" if v["missing"] == v["total"] else ""
    print(f"{v['missing']:8,d} {v['total']:7,d} {v['bytes'] / 2**30:7.1f}   {k}{full}")

json.dump({k: v for k, v in gaps.items()}, open("gap_ledger.json", "w"), indent=1)
print("\nwritten: gap_ledger.json")
