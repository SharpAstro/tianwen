"""Retry the lights a 30 s BLIND solve missed, with a position and a scale. READ ONLY on the archive.

Positions are the targets the folder names state. Scales come from what the same camera SOLVED to that
month where one exists (the ASI462MC at 3.40"/px through February 2021; the ASI183MM Rosette s2 LUM at
3.584), else from the focal length in the folder name and the pixel size (arcsec/px = 206.265 * um /
mm). A hinted solve that succeeds is appended to cache-solves.csv, where a later row for the same frame
wins, so the verdict pass picks it up; one that fails is appended too, marked hinted, so it is not
retried blindly again.
"""
import csv
import re
import subprocess
import time

TIANWEN = r'C:/Users/SebastianGodelet/source/repos/sharpastro/tianwen/src/TianWen.Cli/bin/Release/net10.0/tianwen.exe'
SOLVES = r'C:/temp/e2/cache-solves.csv'
SOLVE_RE = re.compile(r'RA=([\d.]+)h Dec=([-\d.]+).*?scale=([\d.]+)')

# (set_dir suffix, sample name, RA hours, Dec degrees, arcsec/px or None, why)
CASES = [
    ('NGC 4945 RGB\\2021-02-24T23_09_08\\rawframes', 'frame_00061.fits', 13.091, -49.468, 3.40,
     'ASI462MC solved 3.40"/px on Cen A and the Pearl Cluster that month'),
    ('Rosette 4min Ha\\Light', 'frame_2021-12-31-1245_0__00006.fits', 6.537, 4.90, 3.584,
     'ASI183MM Rosette s2 LUM solved 3.584"/px'),
    ('Saturn Nebula\\Light', 'frame_2022-08-27-1229_1_Green_00033.fits', 21.070, -11.363, None,
     'NGC 7009; focal length unknown, position only'),
    ('Seagull Nebula 35mm 240s HaOIII\\Light', 'frame_2022-02-05-1146_0__00008.fits', 7.07, -10.45, 206.265 * 3.76 / 35,
     'IC 2177; 35 mm on 3.76 um'),
    ('Vela SNR 35mm RGB\\Light', 'frame_2022-03-03-1152_7__00037.fits', 8.583, -45.18, 206.265 * 3.76 / 35,
     'Vela SNR; 35 mm on 3.76 um'),
    ('Vela SNR 35mm RGB\\Light', 'frame_2022-03-03-1210_8__00068.fits', 8.583, -45.18, 206.265 * 3.76 / 35,
     'Vela SNR; 35 mm on 3.76 um'),
    ('LeHance 60s -10d\\2025-01-14\\Light\\12_06_47Z\\rawframes', 'frame_00123.fits', 10.751, -59.68, 206.265 * 2.9 / 24,
     'Eta Carinae; 24 mm on 2.9 um'),
    ('Jellyfish Cluster\\Light', 'frame_2022-08-27-1131_8_Blue_00011.fits', None, None, None,
     'position unknown: a longer blind attempt'),
]

gap = [r for r in csv.DictReader(open(r'C:/temp/e2/stage0-gap.csv', encoding='utf-8'))]

with open(SOLVES, 'a', newline='', encoding='utf-8') as fh:
    w = csv.writer(fh)
    for suffix, sample, ra, dec, scale, why in CASES:
        path = next(r['path'] for r in gap if r['path'].endswith(suffix + '\\' + sample) or r['path'].endswith(sample) and suffix.split('\\')[0] in r['path'])
        args = [TIANWEN, 'solve', path, '--range', '0.1']
        if ra is not None:
            args += ['--search-origin', f'{ra},{dec}', '--search-radius', '10']
        if scale is not None:
            args += ['--scale', f'{scale:.3f}']
        t0 = time.time()
        try:
            p = subprocess.run(args, capture_output=True, timeout=180, encoding='utf-8', errors='replace')
            m = SOLVE_RE.search(p.stdout)
            result = f'{m.group(1)}h {m.group(2)}d {m.group(3)}"/px' if m else 'no solution (hinted)'
        except subprocess.TimeoutExpired:
            result = 'timeout 180s (hinted)'
        secs = time.time() - t0
        w.writerow([path, result, f'{secs:.1f}'])
        fh.flush()
        print(f'{result:34s} {secs:5.0f}s  {suffix[:45]:45s} ({why})', flush=True)
