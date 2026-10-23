"""The ring around a removed bright star, which neither eval reads (StarRemovalEval checks the 3 px core, the real-star check
compares the core with 6 to 10 px): on A's draws, every unsaturated injected star at 100 to 1000 sigma (by the scorer's own
significance: the injected 3x3 core over the draw's sigma plane) at least 26 px inside the tile, each channel's mean
residual (output minus plate, in the draw's sigma) over the annulus 3 < r <= 6 px. Reported per model: the mean per
channel, and the share of stars with ANY channel's annulus below -1 sigma (dug) or above +1 (left). Measured first on the
owner's eye, 2026-10-18 (a violet ring where the brightest star was). Glue until the scorer carries it.

    python ring_annulus.py --export <random export> --eval-root <r2-eval> <output-folder-name> [...]
    (the outputs are <eval-root>/<name>/on-random)
"""
import argparse
import json

import numpy as np

ap = argparse.ArgumentParser()
ap.add_argument("--export", required=True, help="the random-mode export A's draws were made in (injections.jsonl, masters)")
ap.add_argument("--eval-root", required=True, help="folder holding <name>/on-random/outputs.jsonl for each model")
ap.add_argument("names", nargs="+")
args = ap.parse_args()
EX = args.export
SIZE, CH, SCALE = 256, 3, 0.6745 * 100.0
yy, xx = np.mgrid[-10:11, -10:11]
RING = (np.hypot(xx, yy) > 3) & (np.hypot(xx, yy) <= 6)


def read(p, shape):
    return np.fromfile(p, "<f2").reshape(shape).astype(np.float32)


def outputs_of(name):
    d = f"{args.eval_root}/{name}/on-random"
    return {r["Tile"]: f"{d}/{r['Output']}" for r in map(json.loads, open(f"{d}/outputs.jsonl", encoding="utf-8"))}


names = args.names
outs = {n: outputs_of(n) for n in names}
stats = {n: [] for n in names}
for line in open(f"{EX}/injections.jsonl", encoding="utf-8"):
    if not line.strip():
        continue
    r = json.loads(line)
    if not all(r["Tile"] in outs[n] for n in names):
        continue
    base = r["Tile"][:-len("_deg000.f16")]
    plate = None
    for st in r["Stars"]:
        if st["Saturated"]:
            continue
        cx, cy = int(round(st["X"])), int(round(st["Y"]))
        if cx < 26 or cy < 26 or cx > SIZE - 27 or cy > SIZE - 27:
            continue
        if plate is None:
            plate = read(f"{EX}/{base}_master.f16", (CH, SIZE, SIZE))
            inp = read(f"{EX}/{r['Tile']}", (CH, SIZE, SIZE))
            sig = read(f"{EX}/{r['Tile'][:-4]}.sigma.f16", (SIZE, SIZE)) / SCALE
            o = {n: read(outs[n][r["Tile"]], (CH, SIZE, SIZE)) for n in names}
            pl, il = plate.mean(0), inp.mean(0)
        s = sig[cy, cx]
        if not s > 0:
            continue
        z = float((il[cy - 1:cy + 2, cx - 1:cx + 2] - pl[cy - 1:cy + 2, cx - 1:cx + 2]).mean() / s)
        if not 100 <= z < 1000:
            continue
        w = (slice(cy - 10, cy + 11), slice(cx - 10, cx + 11))
        for n in names:
            d = (o[n][:, w[0], w[1]] - plate[:, w[0], w[1]]) / sig[w][None]
            stats[n].append([d[c][RING].mean() for c in range(CH)])

print("bright unsaturated injected stars (100-1000 sigma), mean residual per channel over the 3 < r <= 6 px annulus, in sigma")
for n in names:
    a = np.array(stats[n])
    m = a.mean(0)
    print(f"{n:30s} n={len(a)}  R/G/B {m[0]:+.2f} / {m[1]:+.2f} / {m[2]:+.2f}   a channel dug past -1: "
          f"{100 * np.mean((a < -1).any(1)):.1f}%   a channel left past +1: {100 * np.mean((a > 1).any(1)):.1f}%")
