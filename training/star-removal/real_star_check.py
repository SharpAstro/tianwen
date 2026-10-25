"""The real-star check: what each star remover leaves at the REAL stars of the 8 validation sessions' masters (the 2026-10-12
store's noise-mode export, r2-real-val), where no truth exists. At every star the plate builder subtracted (first pass,
unsaturated, inside the 16 px rim), by its catalogued significance band: what is left, the output's 3x3 core less the
median of a ring 6 to 10 px out, in the tile's own noise sigma there (the master's noise plane). A star removed well leaves
about 0; over +1 is light left, under -1 a dig. The ring carries the sky's own texture, alike for every model, so the
models are compared with each other here, not with a truth. Run with the outputs' folder names to compare. Glue only.
After the pooled table comes one table per outcome (% flat, % left, % dug) of the bright band (100 to 1,000 sigma) read
PER VALIDATION SESSION, as docs/plans/star-remover-training.md's "R2w's read" tabulates it: a session's views (the night
and its flip halves, in the order night / flip=a / flip=b) share a row, their figures joined by " / ".

    python real_star_check.py --export <real-val export> --plates <bake>/starless/plates --eval-root <r2-eval> input <name> [...]
"""
import argparse
import csv
import json
from collections import defaultdict

import numpy as np

ap = argparse.ArgumentParser()
ap.add_argument("--export", required=True, help="the real-val noise-mode export (injections.jsonl, tiles/, sigma planes)")
ap.add_argument("--plates", required=True, help="the bake's starless/plates folder (<session>_plate.stars.csv)")
ap.add_argument("--eval-root", required=True, help="folder holding <name>/on-real-val/outputs.jsonl for each model")
ap.add_argument("names", nargs="*", help="'input' or an output folder name under --eval-root; default input")
args = ap.parse_args()
EXPORT, PLATES, OUT = args.export, args.plates, args.eval_root
SIZE, CH, RIM, SCALE = 256, 3, 16, 0.6745 * 100.0
BANDS = ((5, 20), (20, 100), (100, 1000))
yy, xx = np.mgrid[-10:11, -10:11]
RING = (np.hypot(xx, yy) >= 6) & (np.hypot(xx, yy) <= 10)


def read(p, shape):
    return np.fromfile(p, "<f2").reshape(shape).astype(np.float32)


def catalogue(sid):
    rows = [r for r in csv.DictReader(open(f"{PLATES}/{sid.replace('/', '_').replace('|', '_')}_plate.stars.csv", encoding="utf-8"))
            if r["outcome"] == "Subtracted" and r["second_pass"] == "0" and r["saturated"] == "0"]
    return np.array([[float(r["x"]), float(r["y"]), float(r["significance"])] for r in rows])


rows = [json.loads(l) for l in open(f"{EXPORT}/injections.jsonl", encoding="utf-8") if l.strip()]
cats = {}
sites = []   # (tile index, cx, cy, band)
BRIGHT = "100-1000 sigma"
# The target folder of a session's bake path, as the plan's per-session table names it; any other target is its folder name.
SESSION_NAMES = {"Lobster-Nebula": "Lobster", "Great-Orion-Nebula": "M42 (ASI294MC)", "HIP-80609": "HIP 80609",
                 "Small-Magellanic-Cloud": "SMC"}


def session_of(sid):
    """(session label, view) of a SessionId '<bake path>|camera|target|filter[|flip=a|b]': the view is '' for the night itself."""
    parts = sid.split("|")
    view = parts[-1] if len(parts) > 1 and parts[-1].startswith("flip=") else ""
    path = parts[0].split("/")
    target = path[-2] if len(path) > 1 else path[0]
    return SESSION_NAMES.get(target, target.replace("-", " ")), view

for i, r in enumerate(rows):
    sid = r["SessionId"]
    if sid not in cats:
        cats[sid] = catalogue(sid)
    c = cats[sid]
    x = c[:, 0] - r["CellX"]
    y = c[:, 1] - r["CellY"]
    for (lo, hi) in BANDS:
        m = (c[:, 2] >= lo) & (c[:, 2] < hi) & (x >= RIM + 10) & (y >= RIM + 10) & (x < SIZE - RIM - 10) & (y < SIZE - RIM - 10)
        for xi, yi in zip(x[m], y[m]):
            sites.append((i, int(round(xi)), int(round(yi)), f"{lo}-{hi} sigma"))
print(f"{len(rows)} real master tiles, {len(sites)} catalogued stars inside the rim "
      + ", ".join(f"{b}: {sum(1 for s in sites if s[3] == b)}" for b in (f"{lo}-{hi} sigma" for lo, hi in BANDS)))

names = args.names or ["input"]
table = {}
by_session = {}
for name in names:
    if name == "input":
        outs = {r["Tile"]: f"{EXPORT}/{r['Tile']}" for r in rows}
    else:
        d = f"{OUT}/{name}/on-real-val"
        outs = {r["Tile"]: f"{d}/{r['Output']}" for r in map(json.loads, open(f"{d}/outputs.jsonl", encoding="utf-8"))}
    left = defaultdict(list)
    bright = defaultdict(list)   # (session, view) -> the bright band's readings, for the per-session tables
    cache = {}
    for i, cx, cy, band in sites:
        t = rows[i]["Tile"]
        if t not in cache:
            cache = {t: (read(outs[t], (CH, SIZE, SIZE)).mean(0), read(f"{EXPORT}/{t[:-4]}.sigma.f16", (SIZE, SIZE)) / SCALE)}
        img, sig = cache[t]
        s = sig[cy, cx]
        if not s > 0:
            continue
        core = img[cy - 1:cy + 2, cx - 1:cx + 2].mean()
        ring = np.median(img[cy - 10:cy + 11, cx - 10:cx + 11][RING])
        left[band].append((core - ring) / s)
        if band == BRIGHT:
            bright[session_of(rows[i]["SessionId"])].append((core - ring) / s)
    table[name] = {b: np.array(v) for b, v in left.items()}
    by_session[name] = {k: np.array(v) for k, v in bright.items()}

print(f"{'':12s}" + "".join(f"{n[:22]:>24s}" for n in names))
for lo, hi in BANDS:
    b = f"{lo}-{hi} sigma"
    for label, f in (("left |<1|", lambda a: 100 * np.mean(np.abs(a) < 1)), ("light >+1", lambda a: 100 * np.mean(a > 1)),
                     ("dug <-1", lambda a: 100 * np.mean(a < -1)), ("median", np.median)):
        print(f"{b:>14s} {label:10s}" + "".join(f"{f(table[n][b]):24.2f}" for n in names))


# Per session. The sessions in the order the export first names them, a session's views night / flip=a / flip=b.
sessions = list(dict.fromkeys(session_of(r["SessionId"]) for r in rows))
labels = list(dict.fromkeys(label for label, _ in sessions))
view_order = lambda v: (v != "", v)


def views_of(label):
    return sorted((v for l, v in sessions if l == label), key=view_order)


def cell(values, f):
    """The figure of each view joined by ' / '; '-' where a view has no bright star."""
    return " / ".join(f"{f(a):.1f}" if len(a) else "-" for a in values)


def stars(values):
    return " / ".join(str(len(a)) for a in values)


first = names[0]
print()
for title, f in (("% flat |<1|", lambda a: 100 * np.mean(np.abs(a) < 1)), ("% left >+1", lambda a: 100 * np.mean(a > 1)),
                 ("% dug <-1", lambda a: 100 * np.mean(a < -1))):
    print(f"per session, {BRIGHT}, {title}")
    print(f"{'session':18s}{'stars':>20s}" + "".join(f"{n[:22]:>26s}" for n in names))
    for label in labels:
        views = views_of(label)
        got = {n: [by_session[n].get((label, v), np.array([])) for v in views] for n in names}
        print(f"{label[:17]:18s}{stars(got[first]):>20s}" + "".join(f"{cell(got[n], f):>26s}" for n in names))
    pooled = {n: [table[n].get(BRIGHT, np.array([]))] for n in names}
    print(f"{'all':18s}{stars(pooled[first]):>20s}" + "".join(f"{cell(pooled[n], f):>26s}" for n in names))
