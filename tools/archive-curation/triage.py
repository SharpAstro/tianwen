"""Triage every unfiled session by what would be needed to file it.

The gap table says how many frames; it does not say whether a session is tractable. A session on a
characterised body with a filter hint and its own calibration is an afternoon; one on an unknown
body with no bias is parked. This reads a handful of headers per session and sorts them that way.
"""
import glob
import os
import collections
import warnings

warnings.filterwarnings("ignore")
from astropy.io import fits  # noqa: E402

import digest_ledger  # noqa: E402

ORG = "d:/astro-organized"

recs = digest_ledger.load()


def isfits(q):
    return q.lower().endswith((".fits", ".fit", ".fts"))


org = {r["digest"] for q, r in recs.items()
       if q.lower().startswith(ORG) and r.get("digest") and isfits(q)}

sess = collections.defaultdict(lambda: {"miss": 0, "paths": []})
for q, r in recs.items():
    ql = q.lower()
    if ql.startswith(ORG) or not r.get("digest") or not isfits(q):
        continue
    if ql.startswith("d:/astro-pics/"):
        rest = q[len("D:/Astro-Pics/"):].split("/")
        key = "D:/Astro-Pics/" + ("/".join(rest[:2]) if rest[0][:4].isdigit() else rest[0])
    elif ql.startswith("d:/astro-unsorted/"):
        key = "D:/Astro-Unsorted/" + q[len("D:/Astro-Unsorted/"):].split("/")[0]
    else:
        continue
    if r["digest"] not in org:
        s = sess[key]
        s["miss"] += 1
        if len(s["paths"]) < 400:
            s["paths"].append(q)

rows = []
for key, s in sess.items():
    cams, types, filt_hint = collections.Counter(), collections.Counter(), ""
    for p in s["paths"][::max(1, len(s["paths"]) // 25)][:25]:
        try:
            with fits.open(p, memmap=False) as h:
                hdr = h[0].header
        except Exception:
            continue
        cams[str(hdr.get("INSTRUME", "?"))[:20]] += 1
        types[str(hdr.get("IMAGETYP", "?")).upper()[:9]] += 1
        if not filt_hint and hdr.get("FILTER"):
            filt_hint = str(hdr["FILTER"])[:14]
    cam = cams.most_common(1)[0][0] if cams else "?"
    has_light = any(t.startswith("LIGHT") for t in types)
    has_cal = any(t.startswith(("BIAS", "DARK", "FLAT")) for t in types)
    rows.append((s["miss"], cam, has_light, has_cal, filt_hint, dict(types), key))

rows.sort(key=lambda r: -r[0])
print(f"{len(rows)} session trees with unfiled FITS\n")
print(f"{'miss':>6} {'camera':20s} {'L':1s} {'C':1s} {'filter':14s} session")
for miss, cam, hl, hc, fh, ty, key in rows[:40]:
    print(f"{miss:6,d} {cam:20s} {'Y' if hl else '-'} {'Y' if hc else '-'} {fh:14s} "
          f"{key.replace('D:/Astro-', '')[:52]}")

print("\nby camera, unfiled frames:")
bycam = collections.Counter()
for miss, cam, *_ in rows:
    bycam[cam] += miss
for k, v in bycam.most_common(12):
    print(f"  {v:7,d}  {k}")
