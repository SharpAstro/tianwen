"""Blocks of absent or zero pixels in an export's tiles (found 2026-11-20 on R4f's check: black squares in target tiles).
    python -I tile_holes.py <export> [<session substring>] [--max N]
Per session: how many target (master) tiles and how many input (deg) tiles hold NaN, and how many hold a run of at
least 400 exact zeros; for the first few such target tiles, the hole's value, size and bounding box, and whether the
same pixels are holes in that cell's input draws."""
import json, os, sys
import numpy as np

EXP = sys.argv[1]
key = sys.argv[2].lower() if len(sys.argv) > 2 and not sys.argv[2].startswith("--") else ""
cap = int(sys.argv[sys.argv.index("--max") + 1]) if "--max" in sys.argv else 10 ** 9
T = 256


def load(path):
    return np.fromfile(path, "<f2").astype(np.float32).reshape(-1, T, T)


by = {}
for line in open(os.path.join(EXP, "tiles-manifest.jsonl"), encoding="utf-8"):
    r = json.loads(line)
    if key in r["SessionId"].lower():
        by.setdefault(r["SessionId"], []).append(r)
shown = 0
tot = {"master": [0, 0, 0], "deg": [0, 0, 0]}
for n, (session, rows) in enumerate(by.items()):
    if n >= cap:
        break
    count = {"master": [0, 0, 0], "deg": [0, 0, 0]}
    for r in rows:
        kind = "master" if r["Frame"] == "master" else "deg"
        t = load(os.path.join(EXP, r["Tile"]))
        nan = ~np.isfinite(t).all(axis=0); zero = (t == 0).all(axis=0)
        count[kind][0] += 1; count[kind][1] += bool(nan.sum() >= 400); count[kind][2] += bool(zero.sum() >= 400)
        if kind == "master" and (nan.sum() >= 400 or zero.sum() >= 400) and shown < 4:
            shown += 1
            hole = nan | zero
            ys, xs = np.nonzero(hole)
            draw = load(os.path.join(EXP, r["Tile"].replace("_master.f16", "_deg000.f16")))
            dh = (~np.isfinite(draw).all(axis=0)) | (draw == 0).all(axis=0)
            print(f"   {r['Tile'][-40:]}: {int(nan.sum())} NaN px, {int(zero.sum())} zero px, box x {xs.min()}-{xs.max()} y {ys.min()}-{ys.max()}; "
                  f"the same pixels in its first input draw: {int((dh & hole).sum())} of {int(hole.sum())} are holes; tile median {np.nanmedian(t):.4f}")
    for k in count:
        for i in range(3):
            tot[k][i] += count[k][i]
    if count["master"][1] or count["master"][2] or count["deg"][1] or count["deg"][2]:
        print(f"{session[:100]}: targets {count['master'][0]}, with NaN {count['master'][1]}, with zeros {count['master'][2]}; inputs {count['deg'][0]}, with NaN {count['deg'][1]}, with zeros {count['deg'][2]}")
print(f"sessions {min(len(by), cap)}; targets {tot['master'][0]}: with a NaN block {tot['master'][1]}, with a zero block {tot['master'][2]}; inputs {tot['deg'][0]}: with a NaN block {tot['deg'][1]}, with a zero block {tot['deg'][2]}")
