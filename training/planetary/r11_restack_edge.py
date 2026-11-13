"""R11 (#2116): each restacked master's limb edge at 0.1 and 0.3 cycles a pixel and its 2-pixel lattice as stacked.

Glue only: both are what planetary sharpen prints with no telescope named (--no-write). A master read once is not read again.
"""
import glob
import json
import os
import re
import subprocess

HERE = "D:/temp/tianwen-scratch/i2116/restack"  # scratch: one folder a session, the logs beside them
EXE = "D:/temp/tianwen-scratch/i2116/bin-batch/tianwen.exe"
EDGE = re.compile(r"the limb's edge at 0\.1 and 0\.3 cycles a pixel ([0-9.]+), ([0-9.]+)")
LATTICE = re.compile(r"the master as stacked: the 2-pixel lattice per channel \(along x, along y, diagonal\) (.*)")

sessions = sorted(json.load(open("D:/Astro-Dataset/planetary/manifest.json", encoding="utf-8"))["sessions"], key=lambda s: s["frames"])
print("| session | planet | size | edge at 0.1 | edge at 0.3 | lattice, largest |")
print("|---|---|---|---|---|---|")
for s in sessions:
    out = f"{HERE}/{re.sub(r'[^A-Za-z0-9]+', '-', s['name']).strip('-')}"
    masters = [m for m in glob.glob(f"{out}/master_*.fits") if "_half" not in m]
    if len(masters) != 1:
        continue
    where = (s["name"] + s["folder"]).lower()
    planet = "saturn" if "saturn" in where else "mars" if "mars" in where else "jupiter"
    log = f"{out}/edge.log"
    if not os.path.exists(log):
        with open(log, "w", encoding="utf-8") as f:
            subprocess.run([EXE, "planetary", "sharpen", masters[0], "--planet", planet, "--no-write"], stdout=f, stderr=subprocess.STDOUT,
                           creationflags=subprocess.CREATE_NO_WINDOW)
    text = open(log, encoding="utf-8", errors="replace").read()
    e, l = EDGE.search(text), LATTICE.search(text)
    lattice = max(float(v) for v in re.findall(r"[0-9.]+e-?[0-9]+", l.group(1))) if l else None
    print(f"| {s['name']} | {planet} | {s['width']}x{s['height']} {s['colorId']} | {e.group(1) if e else 'not read'} | {e.group(2) if e else 'not read'} | "
          f"{f'{lattice:.2e}' if lattice is not None else 'not read'} |")
