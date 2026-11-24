"""Look at a real-pairs export (tianwen dataset degrade --mode stars --background real) at 1:1, before anything trains on it.

    python -I realpair_export_look.py <export root> <out prefix> [cells per sheet, default 3]

For each session of the export: three of its cells (the one whose mask counts the most pixels, the median one and the one
that counts the fewest), one row a cell: the input (the master's own tile), the target (the plate's tile), input less
target at the same stretch (what the pair asks a remover to take out) and the loss mask over the input (counted pixels in
green). Tiles are 256 px shown at 3x, nearest neighbour, each panel captioned. Prints, per session, the share of pixels
inside the rim the masks count, and what share of the input-less-target light lies on counted pixels. Reads only.
"""
import json
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

TILE, RIM, ZOOM, BAR = 256, 16, 3, 18
root, prefix = sys.argv[1], sys.argv[2]
per_sheet = int(sys.argv[3]) if len(sys.argv) > 3 else 3


def tile(rel):
    raw = np.fromfile(os.path.join(root, rel), "<f2").astype(np.float32)
    return raw.reshape(-1, TILE, TILE)


def grey(a, lo, hi):
    v = np.clip((a - lo) / max(hi - lo, 1e-9), 0, 1)
    return (v * 255).astype(np.uint8)


rows = [json.loads(line) for line in open(os.path.join(root, "degradations.jsonl"), encoding="utf-8") if line.strip()]
sessions = {}
for r in rows:
    sessions.setdefault(r["SessionId"], []).append(r)

for n, (session, cells) in enumerate(sessions.items()):
    stats = []
    for r in cells:
        keep = tile(r["Tile"][:-len(".f16")] + ".keep.f16")[0] > 0.5
        inp = tile(r["Tile"]).mean(axis=0)
        clean = tile(os.path.dirname(r["Tile"]) + f"/x{r['CellX']}_y{r['CellY']}_master.f16").mean(axis=0)
        inner = np.zeros_like(keep)
        inner[RIM:-RIM, RIM:-RIM] = True
        diff = inp - clean
        light = np.clip(diff, 0, None)
        on = float(light[keep & inner].sum()) / max(float(light[inner].sum()), 1e-12)
        stats.append((float(keep[inner].mean()), on, r, inp, clean, keep))
    shares = np.array([s[0] for s in stats])
    onmask = np.array([s[1] for s in stats])
    print(f"{session}: {len(cells)} cells; the masks count {100 * shares.mean():.1f} % of the pixels inside the rim "
          f"(least {100 * shares.min():.1f}, most {100 * shares.max():.1f}); of the light the input holds over the target, "
          f"{100 * np.median(onmask):.0f} % (median cell) lies on counted pixels")
    order = sorted(range(len(stats)), key=lambda i: stats[i][0])
    picks = [order[-1], order[len(order) // 2], order[0]][:per_sheet]
    side = TILE * ZOOM
    sheet = Image.new("RGB", (4 * side + 18, len(picks) * (side + BAR + 6)), (40, 40, 40))
    for j, i in enumerate(picks):
        share, on, r, inp, clean, keep = stats[i]
        lo, hi = np.percentile(inp, 1), np.percentile(inp, 99.7)
        base = grey(inp, lo, hi)
        over = np.stack([base, base, base], axis=-1)
        over[keep] = (0.45 * over[keep] + 0.55 * np.array([0, 255, 0])).astype(np.uint8)
        d = inp - clean
        span = max(float(np.percentile(np.abs(d), 99.9)), 1e-6)
        cell = f"x{r['CellX']} y{r['CellY']}"
        panels = [(f"input: the master's own tile, {cell}", np.stack([base] * 3, axis=-1)),
                  ("target: the plate's tile, same stretch", np.stack([grey(clean, lo, hi)] * 3, axis=-1)),
                  (f"input less target, grey = 0, +-{span:.3f}", np.stack([grey(d, -span, span)] * 3, axis=-1)),
                  (f"loss mask in green: {100 * share:.1f} % counted, {100 * on:.0f} % of the light", over)]
        for k, (cap, img) in enumerate(panels):
            t = Image.new("RGB", (side, side + BAR), (0, 0, 0))
            ImageDraw.Draw(t).text((4, 3), cap, fill=(255, 255, 0))
            t.paste(Image.fromarray(img).resize((side, side), Image.NEAREST), (0, BAR))
            sheet.paste(t, (k * (side + 6), j * (side + BAR + 6)))
    out = f"{prefix}-{n + 1}.png"
    sheet.save(out)
    print(f"  sheet {out}: most, median and least counted cells")
