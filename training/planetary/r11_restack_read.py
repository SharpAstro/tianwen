"""R11 (#2116): each restacked session's halves judged against each other, and the table of w = 2c / (1 + c) per band.

Glue only: the band correlations are planetary judge's. A session judged once is not judged again.
"""
import glob
import json
import os
import re
import subprocess

HERE = "D:/temp/tianwen-scratch/i2116/restack"  # scratch: one folder a session, the logs beside them
EXE = "D:/temp/tianwen-scratch/i2116/bin-batch/tianwen.exe"
BAND = re.compile(r"^\s+band (\d):\s+(-?[0-9.]+)", re.M)
KEPT = re.compile(r"stacked (\d+)/(\d+) frames")
PLANET = re.compile(r"balanced to (\w+)'s colour|planet (\w+)", re.I)

sessions = sorted(json.load(open("D:/Astro-Dataset/planetary/manifest.json", encoding="utf-8"))["sessions"], key=lambda s: s["frames"])
print("| session | folder | kept | band 1 | band 2 | band 3 | band 4 |")
print("|---|---|---|---|---|---|---|")
for s in sessions:
    out = f"{HERE}/{re.sub(r'[^A-Za-z0-9]+', '-', s['name']).strip('-')}"
    if not os.path.exists(f"{out}/stack.log"):
        continue
    a, b = glob.glob(f"{out}/master_*_halfA.fits"), glob.glob(f"{out}/master_*_halfB.fits")
    stack = open(f"{out}/stack.log", encoding="utf-8", errors="replace").read()
    kept = KEPT.search(stack)
    if len(a) != 1 or len(b) != 1:
        print(f"| {s['name']} | {s['folder']} | no halves | | | | |")
        continue
    log = f"{out}/judge-halves.log"
    if not os.path.exists(log) or not BAND.search(open(log, encoding="utf-8", errors="replace").read()):
        with open(log, "w", encoding="utf-8") as f:
            subprocess.run([EXE, "planetary", "judge", a[0], b[0]], stdout=f, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)
    c = {int(k): float(v) for k, v in BAND.findall(open(log, encoding="utf-8", errors="replace").read())}
    w = [f"{2 * c[i] / (1 + c[i]):.3f}" if i in c else "not read" for i in (1, 2, 3, 4)]
    print(f"| {s['name']} | {s['folder'].split('/')[-2] if s['folder'].endswith('Light') else s['folder'].split('/')[-1]} | {kept.group(1) if kept else '?'} | " + " | ".join(w) + " |")
