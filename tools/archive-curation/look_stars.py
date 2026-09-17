"""Star counts on the lights no solve could place: the bake registers by star quads, so enough real
stars (FWHM well above a hot pixel's ~1.7 px) is what decides whether such a set is usable. READ ONLY."""
import csv
import os
import re
import subprocess

TIANWEN = r'C:/Users/SebastianGodelet/source/repos/sharpastro/tianwen/src/TianWen.Cli/bin/Release/net10.0/tianwen.exe'
WANT = [os.path.join('NGC 4945 RGB', '2021-02-24T23_09_08', 'rawframes'),
        os.path.join('Saturn Nebula', 'Light'),
        os.path.join('LeHance 60s -10d', '2025-01-14', 'Light', '12_06_47Z', 'rawframes'),
        os.path.join('Jellyfish Cluster', 'Light')]

by_dir = {}
for r in csv.DictReader(open(r'C:/temp/e2/stage0-gap.csv', encoding='utf-8')):
    d = os.path.dirname(r['path'])
    if any(d.endswith(w) for w in WANT):
        by_dir.setdefault(d, []).append(r['path'])

for d, ps in sorted(by_dir.items()):
    ps.sort(key=str.lower)
    for p in [ps[0], ps[len(ps) // 2], ps[-1]]:
        out = subprocess.run([TIANWEN, 'image', 'stats', p], capture_output=True, encoding='utf-8', errors='replace').stdout
        stars = re.search(r'stars=(\d+)', out)
        fwhm = re.search(r'FWHM=([\d.]+)px', out)
        snr = re.search(r'SNR=([\d.]+)', out)
        print(f"  stars={stars.group(1) if stars else '?':>5s} FWHM={fwhm.group(1) if fwhm else '-':>5s} "
              f"SNR={snr.group(1) if snr else '-':>6s}  {os.path.relpath(p, 'D:/Astro-Pics')[-75:]}")
