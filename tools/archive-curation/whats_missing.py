"""For one Astro-Pics session, WHICH frames are not in Organized, broken down by what they are.

"2,365 of 3,461 missing" is not actionable on its own: a session whose lights are all filed and whose
calibration is not needs a different fix from one that was never touched. This says which.
"""
import glob
import json
import sys
import collections
import warnings

warnings.filterwarnings("ignore")
from astropy.io import fits  # noqa: E402

LEDGER = "D:/Astro-Reports/digests.jsonl"
ORG = "d:/astro-organized"

org = set()
path_of = {}
for line in open(LEDGER, encoding="utf-8"):
    try:
        r = json.loads(line)
    except Exception:
        continue
    q = r["path"].replace("\\", "/")
    if q.lower().startswith(ORG) and r.get("digest"):
        org.add(r["digest"])
    path_of[q] = r

for D in sys.argv[1:]:
    fs = sorted(glob.glob(D + "/**/*.fit*", recursive=True))
    have = collections.Counter()
    miss = collections.Counter()
    for p in fs:
        q = p.replace("\\", "/")
        rec = path_of.get(q)
        try:
            with fits.open(p, memmap=False) as h:
                hdr = h[0].header
        except Exception:
            continue
        key = (str(hdr.get("IMAGETYP", "?"))[:12],
               round(float(hdr.get("EXPTIME", 0) or 0), 2),
               str(hdr.get("OBJECT", "?"))[:24])
        if rec and rec.get("digest") in org:
            have[key] += 1
        else:
            miss[key] += 1
    print(f"\n=== {D}")
    print(f"  {sum(have.values())} already in Organized, {sum(miss.values())} not")
    print(f"  {'IMAGETYP':12s} {'exp':>9s}  {'object':24s} {'filed':>6s} {'MISSING':>8s}")
    for k in sorted(set(have) | set(miss), key=lambda k: -(miss[k] + have[k])):
        if not miss[k]:
            continue
        print(f"  {k[0]:12s} {k[1]:9.2f}  {k[2]:24s} {have[k]:6d} {miss[k]:8d}")
