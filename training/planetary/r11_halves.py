"""R11 step 1 (#2116): is band 2 of the real EdgeHD stack repeatable detail or noise, read off the stack's own two halves.

  python r11_halves.py real          the real capture stacked with --auto --halves, its halves judged against each other
  python r11_halves.py twins [n ...] three held twins (d024, e002, f002 by default) re-made from their plan rows, stacked with
                                     --halves; the halves judged against each other and the full master against its truth (green)
  python r11_halves.py read          the table: each band's correlation c between the halves, w = 2c / (1 + c), the twins' r against the
                                     truth, and Spearman of sqrt(w) against r over the twins' bands 1 to 4 (the kill line, 0.8)

Glue only: the stacking, the halves and every band correlation are the CLI's (planetary stack --halves, planetary judge). Scratch is
SCR (D:/temp/tianwen-scratch/i2116); a finished log is never run again; a stop file SCR/r11.stop ends a run between children. The
build is R10's (r10_gains.BIN, its BUILT_FROM.txt beside it).
"""
import glob
import math
import os
import re
import sys

import r10_gains as r10

SCR = "D:/temp/tianwen-scratch/i2116"
r10.STOP = f"{SCR}/r11.stop"
# The copy the 2026-10-11 chain stacked (final-1011/run.sh): the same bytes as r10.EDGE, in a folder of the same name.
REAL = "D:/temp/tianwen-scratch/i1391/viewer/Jupiter-1100 EdgeHD + ASI678MC-f14-DS/2023-11-11-0244_2-MPO.ser"
TWINS = ["d024", "e002", "f002"]
UTC = "2023-11-11T02:44:17Z"
BAND = re.compile(r"^\s+band (\d):\s+(-?[0-9.]+)\s+(-?[0-9.]+)\s+(-?[0-9.]+)", re.M)


def halves(out):
    a, b = sorted(glob.glob(f"{out}/master_*_halfA.fits")), sorted(glob.glob(f"{out}/master_*_halfB.fits"))
    if len(a) != 1 or len(b) != 1:
        raise SystemExit(f"{out}: expected one halfA and one halfB, found {a} {b}")
    return a[0], b[0]


def judge(master, reference, log):
    if not r10.child([r10.BIN, "planetary", "judge", "--planet", "jupiter", "--utc", UTC, master, reference], log):
        raise SystemExit(f"judge failed, see {log}")


def real(keep=None):
    """keep None: the plain stack's own keep (the best 10 %, what the twins' masters are stacked at). keep 0.5: what --auto keeps
    for this capture (2,696 of 5,392, the 2026-10-11 chain's master)."""
    out = f"{SCR}/real" if keep is None else f"{SCR}/real-keep{round(keep * 100)}"
    extra = [] if keep is None else ["--keep", str(keep)]
    # Not --auto, which refuses --halves (and --no-sharpen): the plain stack at the measured defaults, the command the twins take.
    # It reads the planet off the folder's name and de-rotates a run of Jupiter by itself, as --auto does.
    if not r10.child([r10.BIN, "planetary", "stack", REAL, "--halves", "--no-sharpen", *extra, "-o", out], f"{out}/stack.log"):
        raise SystemExit(f"the real stack failed, see {out}/stack.log")
    judge(*halves(out), f"{out}/judge-halves.log")


def twin(n):
    out = f"{SCR}/twins/{n}"
    ser = f"{out}/{n}.ser"
    if not r10.child([r10.BIN, "planetary", "degrade", r10.EDGE, *r10.degrade_args(n), "-o", ser], f"{out}/degrade.log"):
        raise SystemExit(f"{n}: degrade failed, see {out}/degrade.log")
    if not r10.child([r10.BIN, "planetary", "stack", ser, "--truth", f"{out}/{n}.truth.fits", "--halves", "--no-sharpen", "-o", out],
                     f"{out}/stack.log"):
        raise SystemExit(f"{n}: stack failed, see {out}/stack.log")
    if os.path.exists(ser):
        os.remove(ser)
    judge(*halves(out), f"{out}/judge-halves.log")
    judge(f"{out}/master_{n}.fits", f"{out}/{n}.truth.g.fits", f"{out}/judge-truth.log")


def bands(log):
    """band -> correlation, from a planetary judge log."""
    return {int(b): float(c) for b, c, _, _ in BAND.findall(open(log, encoding="utf-8").read())}


def w(c):
    return 2 * c / (1 + c) if c > -1 else float("nan")


def ranks(v):
    order = sorted(range(len(v)), key=lambda i: v[i])
    r = [0.0] * len(v)
    i = 0
    while i < len(order):
        j = i
        while j + 1 < len(order) and v[order[j + 1]] == v[order[i]]:
            j += 1
        for k in range(i, j + 1):
            r[order[k]] = (i + j) / 2
        i = j + 1
    return r


def spearman(x, y):
    rx, ry = ranks(x), ranks(y)
    mx, my = sum(rx) / len(rx), sum(ry) / len(ry)
    num = sum((a - mx) * (b - my) for a, b in zip(rx, ry))
    den = math.sqrt(sum((a - mx) ** 2 for a in rx) * sum((b - my) ** 2 for b in ry))
    return num / den if den else float("nan")


def read():
    print("capture | band: c between the halves, w = 2c/(1+c)" + " | r against the truth (twins)")
    xs, ys = [], []
    for n in ["real", "real-keep50", *TWINS]:
        out = f"{SCR}/{n}" if n.startswith("real") else f"{SCR}/twins/{n}"
        if not os.path.exists(f"{out}/judge-halves.log"):
            print(f"{n}: not run")
            continue
        c = bands(f"{out}/judge-halves.log")
        r = bands(f"{out}/judge-truth.log") if not n.startswith("real") else {}
        for b in sorted(c):
            line = f"{n:11s} band {b}: c {c[b]:.3f}  w {w(c[b]):.3f}"
            if b in r:
                line += f"  sqrt(w) {math.sqrt(max(w(c[b]), 0)):.3f}  r {r[b]:.3f}"
                if b <= 4:
                    xs.append(math.sqrt(max(w(c[b]), 0)))
                    ys.append(r[b])
            print(line)
    if xs:
        print(f"V: Spearman of sqrt(w) against r over {len(xs)} twin bands (1 to 4): {spearman(xs, ys):.3f} (the kill line: at least 0.8)")


if __name__ == "__main__":
    os.makedirs(SCR, exist_ok=True)
    what = sys.argv[1] if len(sys.argv) > 1 else "read"
    if what == "real":
        real(float(sys.argv[2]) if len(sys.argv) > 2 else None)
    elif what == "twins":
        for name in sys.argv[2:] or TWINS:
            twin(name)
    elif what == "read":
        read()
    else:
        raise SystemExit(__doc__)
