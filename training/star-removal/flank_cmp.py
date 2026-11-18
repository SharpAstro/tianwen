"""Real against drawn star flanks, both in the stretched tile domain at matched peak height (R3w item 5, R3y Q1).

    python -I flank_cmp.py <session key> <drawn export root> [--min-beta B]

Real: the validation masters' tiles (`r2-real-val`, an export of the real masters) at the plate catalogue's unsaturated
stars of 50 sigma and over. Drawn: the twin's tiles of <drawn export root>, from its injections.jsonl (Draw 0, no flip
sessions). Sky is the median at 12 to 20 px; stars are binned by green's peak; each ring is a share of the core (0 to
0.8 px); medians per channel. A star with a brighter neighbour in its 41 px stamp is skipped.

--min-beta B keeps only the drawn stars whose GREEN Beta (injections.jsonl, Beta[1]) is at least B: R3y's Q1 population
(the stars drawn at beta 8 and over). It also prints, per ring and channel, the share of the kept drawn stars under
real's median, and the same share over all drawn stars of the bin.

The 25th percentile of the first two rings (1.6-2.5 and 2.5-3.5 px) is printed after the medians ("q25").

Paths: TIANWEN_DATASET (the dataset root holding starless/plates) and TIANWEN_REAL_VAL_ROOT (the folder holding r2-real-val),
with the DE1 defaults below. The beta 8 and 20 exports of item 5 are made by flank-exports.ps1.
"""
import csv, json, os, sys, numpy as np

S = os.environ.get("TIANWEN_REAL_VAL_ROOT", "C:/temp/tianwen-scratch")
PL = os.environ.get("TIANWEN_DATASET", "D:/Astro-Dataset/2026-10-12-full") + "/starless/plates"
args = [a for a in sys.argv[1:] if not a.startswith("--")]
min_beta = float(sys.argv[sys.argv.index("--min-beta") + 1]) if "--min-beta" in sys.argv else None
if min_beta is not None:
    args.remove(sys.argv[sys.argv.index("--min-beta") + 1])
key, DR = args[0], args[1]
rd = lambda p: np.fromfile(p, "<f2").reshape(3, 256, 256).astype(np.float32)
yy, xx = np.mgrid[-20:21, -20:21]
rr = np.hypot(yy, xx)
sky = (rr >= 12) & (rr < 20)
E = [0, 0.8, 1.6, 2.5, 3.5, 4.5, 6, 8]
BINS = [(0.05, 0.12), (0.12, 0.25), (0.25, 0.5), (0.5, 0.9)]
acc = {}


def take(img, x, y, who):
    x, y = int(round(x)), int(round(y))
    if not (24 <= x < 232 and 24 <= y < 232):
        return
    st = img[:, y - 20:y + 21, x - 20:x + 21]
    st = st - np.median(st[:, sky], axis=1)[:, None, None]
    pk = st[1, 19:22, 19:22].max()
    if st[1].max() > pk * 1.001:  # a brighter neighbour in the stamp
        return
    for b in BINS:
        if b[0] <= pk < b[1]:
            acc.setdefault((who, b), []).append(
                [[st[c][(rr >= a) & (rr < e)].mean() / st[c][rr < 0.8].mean() for a, e in zip(E, E[1:])] for c in range(3)])


for l in open(f"{DR}/injections.jsonl", encoding="utf-8"):
    r = json.loads(l)
    if key not in r["SessionId"] or "flip" in r["SessionId"] or r["Draw"] != 0:
        continue
    img = rd(f"{DR}/{r['Tile']}")
    for s in r["Stars"]:
        if s["Saturated"] or s["Lobe"]:
            continue
        take(img, s["X"], s["Y"], "drawn")
        if min_beta is not None and s["Beta"][1] >= min_beta:
            take(img, s["X"], s["Y"], "kept")
cat = None
for l in open(f"{S}/r2-real-val/injections.jsonl", encoding="utf-8"):
    r = json.loads(l)
    if key not in r["SessionId"] or "flip" in r["SessionId"]:
        continue
    if cat is None:
        stem = r["Tile"].split("/")[1]
        cat = [(float(s["x"]), float(s["y"])) for s in csv.DictReader(open(f"{PL}/{stem}_plate.stars.csv", encoding="utf-8"))
               if s["saturated"] == "0" and s["second_pass"] == "0" and float(s["significance"]) >= 50]
    img = rd(f"{S}/r2-real-val/{r['Tile']}")
    for x, y in cat:
        if r["CellX"] <= x < r["CellX"] + 256 and r["CellY"] <= y < r["CellY"] + 256:
            take(img, x - r["CellX"], y - r["CellY"], "real")
print(key, "  ratio to the core (0-0.8 px), rings", " ".join(f"{a}-{b}" for a, b in zip(E[1:], E[2:])))
whos = ("real", "drawn") + (("kept",) if min_beta is not None else ())
for b in BINS:
    for c, cn in enumerate("RGB"):
        rv = acc.get(("real", b))
        rmed = np.median(np.array(rv)[:, c], 0) if rv else None
        for who in whos:
            v = acc.get((who, b))
            if not v:
                continue
            a = np.array(v)[:, c]
            line = (f"  peak {b[0]}-{b[1]} {cn} {who:5s} n={len(v):5d} " + " ".join(f"{t:7.4f}" for t in np.median(a, 0)[1:])
                    + "   q25 " + " ".join(f"{t:7.4f}" for t in np.percentile(a, 25, axis=0)[2:4]))
            if who != "real" and rmed is not None:
                line += "   under real median (1.6-2.5, 2.5-3.5) " + " ".join(f"{(a[:, i] < rmed[i]).mean():5.2f}" for i in (2, 3))
            print(line)
