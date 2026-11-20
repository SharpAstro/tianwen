"""Which bright cells can take the stored starless plate as their training target (R4g, docs/plans/star-remover-training.md).

    python -I bright_plate_cells.py --cells <tianwen dataset bright-cells list> --plates <starless root>/plates
        --train <train session list> --val <val session list> --out <cell list>
        [--max-inpainted 10] [--max-subtracted 20] [--max-left-100 0] [--exclude-session <substring> ...]

On bright structured nebula the synthetic background is no truth: it redraws a sharp edge's fine scales as random texture,
which empties the dark side to black. The stored plate is one, where the plate builder had little to do. A listed cell of a
session in either list passes when, over its 256 px, the builder inpainted at most --max-inpainted percent and subtracted
at most --max-subtracted percent of the pixels (its two masks beside the plate), and left at most --max-left-100 sources of
100 sigma and over unsubtracted (its star table). That leaves out a cluster's core, a bright star's halo and a cell whose
subtracted halos left rings.

Writes the passing cells as a cell list (x TAB y TAB session id, what the export's --extra-cells reads) and prints each
session's listed and passing counts with the split it is in.
"""
import argparse
import csv
import glob
import gzip
import io
import os
from collections import defaultdict

import numpy as np
from astropy.io import fits

TILE = 256


def session_list(path):
    with open(path, encoding="utf-8-sig") as fh:
        return {line.strip() for line in fh if line.strip() and not line.startswith("#")}


def read_mask(path):
    """A builder's mask as a 2-D boolean frame (any channel set)."""
    with gzip.open(path, "rb") as fh:
        mask = np.asarray(fits.getdata(io.BytesIO(fh.read()))) != 0
    return mask if mask.ndim == 2 else mask.any(axis=0)


def is_excluded(session, substrings):
    """Whether --exclude-session names the session (a substring of its id, case ignored): its flip sides go with it."""
    return any(s.lower() in session.lower() for s in substrings)


def passes(inpainted_pct, subtracted_pct, left_100, max_inpainted, max_subtracted, max_left_100):
    """The gate itself, on a cell's three readings."""
    return inpainted_pct <= max_inpainted and subtracted_pct <= max_subtracted and left_100 <= max_left_100


def cell_readings(inpainted, subtracted, xs, ys, significance, is_left, x, y):
    """A cell's share inpainted and subtracted, in percent, and its sources left unsubtracted at 100 sigma and over."""
    inside = (xs >= x) & (xs < x + TILE) & (ys >= y) & (ys < y + TILE)
    return (100.0 * float(inpainted[y:y + TILE, x:x + TILE].mean()), 100.0 * float(subtracted[y:y + TILE, x:x + TILE].mean()),
            int((inside & is_left & (significance >= 100.0)).sum()))


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--cells", required=True)
    p.add_argument("--plates", required=True)
    p.add_argument("--train", required=True)
    p.add_argument("--val", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--max-inpainted", type=float, default=10.0)
    p.add_argument("--max-subtracted", type=float, default=20.0)
    p.add_argument("--max-left-100", type=int, default=0)
    p.add_argument("--exclude-session", action="append", default=[],
                   help="a substring of a session id to leave out whole (a session whose sky a scoring master may hold); repeatable")
    a = p.parse_args()

    split = {s: "train" for s in session_list(a.train)}
    split.update({s: "val" for s in session_list(a.val)})
    for session in sorted(s for s in split if is_excluded(s, a.exclude_session)):
        print(f"   left out by name: {session}")
        del split[session]
    cells = defaultdict(list)
    with open(a.cells, encoding="utf-8-sig") as fh:
        for line in fh:
            if line.strip() and not line.startswith("#"):
                x, y, session = line.rstrip("\n").split("\t")
                if session in split:
                    cells[session].append((int(x), int(y)))

    kept, counts = [], defaultdict(int)
    for session in sorted(cells):
        stem = os.path.join(a.plates, glob.escape(session.replace("/", "_").replace("|", "_")))
        table = glob.glob(stem + "_plate.stars.csv")
        if not table:
            print(f"   no plate: {session}")
            continue
        inpainted = read_mask(glob.glob(stem + "_plate.inpainted.fits.gz")[0])
        subtracted = read_mask(glob.glob(stem + "_plate.subtracted.fits.gz")[0])
        with open(table[0], encoding="utf-8") as fh:
            rows = list(csv.DictReader(fh))
        xs = np.array([float(r["x"]) for r in rows])
        ys = np.array([float(r["y"]) for r in rows])
        significance = np.array([float(r["significance"]) for r in rows])
        is_left = np.array([r["outcome"] != "Subtracted" for r in rows])
        passing = 0
        for x, y in cells[session]:
            if y + TILE > inpainted.shape[0] or x + TILE > inpainted.shape[1]:
                continue
            if passes(*cell_readings(inpainted, subtracted, xs, ys, significance, is_left, x, y),
                      a.max_inpainted, a.max_subtracted, a.max_left_100):
                kept.append((x, y, session))
                passing += 1
        counts[split[session]] += passing
        print(f"{passing:4d} of {len(cells[session]):4d}  {split[session]:5s}  {session}")

    with open(a.out, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(f"# bright cells whose stored plate stands as the target: inpainted at most {a.max_inpainted:g} %, subtracted at most "
                 f"{a.max_subtracted:g} %, at most {a.max_left_100} source(s) left at 100 sigma and over\n")
        fh.write(f"# from {os.path.basename(a.cells)}, the sessions of {os.path.basename(a.train)} and {os.path.basename(a.val)}"
                 + (f", less those named {', '.join(a.exclude_session)}" if a.exclude_session else "") + "\n")
        for x, y, session in kept:
            fh.write(f"{x}\t{y}\t{session}\n")
    print(f"passing {len(kept)} of {sum(len(v) for v in cells.values())} listed cells: {counts['train']} train, {counts['val']} val")
    print(a.out)


if __name__ == "__main__":
    main()
