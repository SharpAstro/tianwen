"""How much of C:/temp/astro is already on D:, by CONTENT?

C:/temp/astro is recorded as a WORKING COPY, which is a claim about intent rather than about bytes.
The ledger now covers it, so the claim is checkable: a file whose digest also appears under an
archive root on D: is genuinely redundant and deleting it loses nothing; one whose digest appears
nowhere else is the only copy and must not be touched.

Reports only. Deletion is the user's to run.
"""
import json
import collections

LEDGER = "D:/Astro-Reports/digests.jsonl"
DROOTS = ("d:/astro-pics/", "d:/astro-organized/", "d:/astro-unsorted/", "d:/astro-processing/")

recs = {}
for line in open(LEDGER, encoding="utf-8"):
    try:
        r = json.loads(line)
    except Exception:
        continue
    recs[r["path"].replace("\\", "/")] = r

d_digests = {r["digest"] for q, r in recs.items()
             if q.lower().startswith(DROOTS) and r.get("digest")}
print(f"ledger {len(recs):,} paths; {len(d_digests):,} distinct digests live on D:\n")

have, missing = [], []
for q, r in recs.items():
    if not q.lower().startswith("c:/temp/"):
        continue
    if not r.get("digest"):
        continue
    (have if r["digest"] in d_digests else missing).append((r.get("size", 0), q))

def gib(rows):
    return sum(s for s, _ in rows) / 2**30

print(f"C:/temp FITS in the ledger: {len(have) + len(missing):,}")
print(f"  ALSO on D: (safe to delete, content preserved) : {len(have):6,d}  {gib(have):8.1f} GiB")
print(f"  ONLY on C: (deleting LOSES it)                 : {len(missing):6,d}  {gib(missing):8.1f} GiB")

by = collections.defaultdict(lambda: [0, 0])
for s, q in missing:
    key = "/".join(q.split("/")[:4])
    by[key][0] += 1
    by[key][1] += s
if missing:
    print("\n  where the C:-only content sits:")
    for k, (n, b) in sorted(by.items(), key=lambda kv: -kv[1][1])[:12]:
        print(f"    {n:6,d}  {b / 2**30:8.1f} GiB  {k}")
