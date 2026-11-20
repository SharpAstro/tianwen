"""Which star is it: the sky position of each star of bright_star_list.tsv off its master's own WCS (2026-11-20, #2292).

    python -I bright_star_names.py <store> [list, default bright_star_list.tsv beside this file]

Per row: the pixel through the master's WCS (its SIP terms included), and the nearest of a short table of bright stars
with the distance in arc minutes. The table holds only the stars the census's fields can contain; a row farther than an
arc minute from all of them is printed as UNNAMED. The check exists because the first write-up named the stars after
their sessions and by rank: the star it called HIP 80609 after its session is Antares, as is the one it called
Ophiuchus, and the Pleiad it called the brightest is Maia."""
import os, sys
import numpy as np
from astropy.io import fits
from astropy.wcs import WCS

# J2000, degrees.
NAMED = {
    "Alcyone": (56.8712, 24.1051), "Merope": (56.5815, 23.9484), "Maia": (56.4567, 24.3678), "Electra": (56.2189, 24.1133),
    "Taygeta": (56.3021, 24.4673), "Atlas": (57.2906, 24.0534), "Pleione": (57.2968, 24.1367), "Celaeno": (56.2009, 24.2895),
    "Asterope": (56.4770, 24.5545), "22 Tau": (56.5121, 24.5279),
    "Antares": (247.3519, -26.4320), "sigma Sco": (245.2971, -25.5928),
    "Alnitak": (85.1897, -1.9426), "iota Ori": (83.8583, -5.9100), "lambda Cen": (173.9454, -63.0198),
}
STORE = sys.argv[1]
LIST = sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "bright_star_list.tsv")
wrong = 0
for line in open(LIST, encoding="utf-8"):
    p = line.rstrip("\n").split("\t")
    if len(p) < 4 or p[0] in ("", "star") or p[0].startswith("#"):
        continue
    star, master, x, y = p[0], p[1], int(p[2]), int(p[3])
    w = WCS(fits.getheader(f"{STORE}/session-masters/{master}.fits"), naxis=2)
    ra, dec = (float(v) for v in w.all_pix2world(x, y, 0))
    name, (nra, ndec) = min(NAMED.items(), key=lambda kv: np.hypot((kv[1][0] - ra) * np.cos(np.radians(dec)), kv[1][1] - dec))
    away = float(np.hypot((nra - ra) * np.cos(np.radians(dec)), ndec - dec)) * 60
    found = name if away <= 1.0 else "UNNAMED"
    agrees = star.startswith(found + " ")
    wrong += not agrees
    print(f"{star:24s} ({x:4d},{y:4d})  RA {ra:9.4f}  Dec {dec:8.4f}  {found:10s} {away:5.2f}'" + ("" if agrees else "   THE LIST SAYS OTHERWISE"))
print("every row's name agrees with its position" if wrong == 0 else f"{wrong} rows whose name and position disagree")
sys.exit(1 if wrong else 0)
