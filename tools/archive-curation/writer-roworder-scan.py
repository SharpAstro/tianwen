"""One representative frame per leaf directory: who wrote it, which way up, and which CFA phase.

The question is whether an OLDER SharpCap stored rows the other way round, because a row flip also
re-phases the Bayer mosaic (RGGB becomes GBRG, GRBG becomes BGGR), so a wrong row-order assumption
is a plausible picture in the wrong colours rather than an error. TianWen's parse DEFAULTS to
TopDown when ROWORDER is absent, which is the case that would be wrong twice over.

Reads the ledger for the directory list rather than walking the archive again, and writes its result
to JSON so the slicing afterwards costs nothing.
"""
import json
import os
import sys
import warnings

warnings.filterwarnings("ignore")
from astropy.io import fits  # noqa: E402

import digest_ledger  # noqa: E402

OUT = "C:/temp/e2/writer-roworder.json"
KEYS = ("SWCREATE", "CREATOR", "PROGRAM", "DATE-OBS", "ROWORDER", "BAYERPAT", "INSTRUME",
        "TELESCOP", "XBAYROFF", "YBAYROFF", "NAXIS1", "NAXIS2", "IMAGETYP")

leaves = {}
for p in digest_ledger.load():
    if p.lower().endswith((".fits", ".fit", ".fts")):
        leaves.setdefault(os.path.dirname(p), p)

print(f"{len(leaves)} leaf directories", flush=True)
rows = []
for i, (d, p) in enumerate(sorted(leaves.items())):
    if not os.path.exists(p):
        continue
    try:
        with fits.open(p, memmap=False) as h:
            hdu = next((x for x in h if x.data is not None), None)
            if hdu is None:
                continue
            hd = hdu.header
            rows.append({"dir": d, "file": os.path.basename(p),
                         **{k: (str(hd[k]) if k in hd else None) for k in KEYS}})
    except Exception as e:
        rows.append({"dir": d, "file": os.path.basename(p), "error": str(e)[:120]})
    if (i + 1) % 100 == 0:
        print(f"  {i + 1}/{len(leaves)}", flush=True)

json.dump(rows, open(OUT, "w", encoding="utf-8"), indent=1)
print(f"wrote {len(rows)} rows to {OUT}")
sys.exit(0)
