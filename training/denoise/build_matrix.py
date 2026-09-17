"""Cut the comparison crops as separate PNG files (region matrix + frame 3x3) and write the page."""
import json
import os
import sys

from PIL import Image

ROOT = "C:/temp/e2/matrix"
HERE = os.path.dirname(os.path.abspath(__file__))
OUT_HTML = sys.argv[1]
FILES = os.path.join(HERE, "matrix-files")
os.makedirs(FILES, exist_ok=True)
SIZE = 448
ROWS = ["flat", "flat_n2n"]
ARMS = ["input", "e30", "prior"]
TRUTH_OFFSET = (-144, -127)
W, H = Image.open(f"{ROOT}/flat/input.png").size
INSET = 200
XS = [INSET, (W - SIZE) // 2, W - INSET - SIZE]
YS = [INSET, (H - SIZE) // 2, H - INSET - SIZE]
REGIONS = [("Statue of Liberty Nebula", 1400, 1450), ("Dense field, the gate's crop", 400, 1400), ("Southern cluster", 1496, 2096)]
GRID = [(f"g{r}{c}", XS[c], YS[r]) for r in range(3) for c in range(3)]

images = {}
def load(key):
    if key not in images:
        images[key] = Image.open(f"{ROOT}/{key}.png")
    return images[key]

def cut(name, source, x, y):
    path = os.path.join(FILES, name + ".png")
    if not os.path.exists(path):
        load(source).crop((x, y, x + SIZE, y + SIZE)).save(path, "PNG", optimize=True)
    return name + ".png"

manifest = {"regions": [], "grid": []}
total = 0
for ri, (name, x, y) in enumerate(REGIONS):
    entry = {"name": name, "x": x, "y": y, "cells": {}}
    for rk in ROWS:
        for ak in ARMS:
            entry["cells"][f"{rk}|{ak}"] = cut(f"r{ri}_{rk}_{ak}", f"{rk}/{ak}", x, y)
    entry["cells"]["sharp|truth"] = cut(f"r{ri}_sharp", "sharp/input", x + TRUTH_OFFSET[0], y + TRUTH_OFFSET[1])
    manifest["regions"].append(entry)
for gname, x, y in GRID:
    entry = {"name": gname, "x": x, "y": y, "cells": {}}
    for rk in ROWS:
        for ak in ARMS:
            entry["cells"][f"{rk}|{ak}"] = cut(f"{gname}_{rk}_{ak}", f"{rk}/{ak}", x, y)
    entry["cells"]["sharp|truth"] = cut(f"{gname}_sharp", "sharp/input", x + TRUTH_OFFSET[0], y + TRUTH_OFFSET[1])
    manifest["grid"].append(entry)
for hero, src in (("hero_after", "flat_n2n/prior"), ("hero_before", "flat/input")):
    path = os.path.join(FILES, hero + ".jpg")
    if not os.path.exists(path):
        # The stack's own autocrop: its CANVASX0/Y0 (1, 2) against the full frame's (-163, -133) puts the
        # covered rectangle at (164, 135), 2846 x 2874, so the canvas ring and its gradient stay out.
        im = load(src).crop((164, 135, 164 + 2846, 135 + 2874))
        im.resize((1400, round(im.size[1] * 1400 / im.size[0])), Image.LANCZOS).save(path, "JPEG", quality=90, optimize=True)
files = sorted(os.listdir(FILES))
total = sum(os.path.getsize(os.path.join(FILES, f)) for f in files)
print(f"{len(files)} files, {total / 1e6:.1f} MB; frame {W}x{H}, grid columns {XS}, rows {YS}")

with open(os.path.join(HERE, "matrix_template.html"), encoding="utf-8") as fh:
    html = fh.read()
html = html.replace("__MANIFEST__", json.dumps(manifest))
with open(OUT_HTML, "w", encoding="utf-8") as fh:
    fh.write(html)
with open(os.path.join(HERE, "matrix-files.json"), "w", encoding="utf-8") as fh:
    json.dump({f: os.path.join("matrix-files", f) for f in files}, fh)
print(f"wrote {OUT_HTML}, {os.path.getsize(OUT_HTML) / 1e3:.0f} KB")
