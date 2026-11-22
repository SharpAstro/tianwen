"""How much a star remover's training pairs change when the export reads another plate store (2026-11-23, #2449).

    python -I plate_pairs_compare.py <export A> <export B> [<reference export for A> [<plates A> <plates B>]]

A and B are two outputs of one `tianwen dataset degrade --mode stars` line over the same sessions, A from the stored
plates and B from the rebuilt ones. Per session: the cells each chose; over the cells both hold, the target tile
(`_master.f16`), the four input draws (`_degNNN.f16`) and the noise plane (`.sigma.f16`), B against A in units of A's
noise plane; and the injected stars (`injections.jsonl`), whether the same stars were drawn at the same places and
how their amplitudes moved, apart for the stars under the tile's noise and for those at 5 times it and over (the
first channel's amplitude over the draw's median noise). With a reference export (the arm's own, which A must repeat),
A's tiles of these sessions are compared with the reference's byte for byte first. With the two plate folders (each
store's `plates`), each session's star lists are compared too: the rows of one that are not in the other, apart from
the columns a hole fill sets or reads (`inpainted`, `core_residual`, `core_bias`, `hole_depth`) and the two that
describe the background. A drawn star takes its brightness and colour from those rows.
"""
import csv
import hashlib
import json
import os
import sys
import numpy as np

SIZE = 256


def tile(path):
    a = np.fromfile(path, dtype=np.float16).astype(np.float32)
    return a.reshape(-1, SIZE, SIZE)


def injections(root):
    out = {}
    with open(os.path.join(root, "injections.jsonl"), encoding="utf-8") as f:
        for line in f:
            r = json.loads(line)
            out[r["Tile"]] = r
    return out


LIST_COLUMNS = ("x", "y", "significance", "amplitude", "width", "sky", "sigma", "outcome", "saturated", "second_pass",
                "model", "amplitudes")


def star_rows(plates, session):
    with open(os.path.join(plates, session + "_plate.stars.csv"), newline="", encoding="utf-8") as f:
        return {tuple(r[k] for k in LIST_COLUMNS) for r in csv.DictReader(f)}


a_root, b_root = sys.argv[1], sys.argv[2]
ref = sys.argv[3] if len(sys.argv) > 3 else None
plates = (sys.argv[4], sys.argv[5]) if len(sys.argv) > 5 else None
sessions = sorted(os.listdir(os.path.join(b_root, "tiles")))
if ref:
    same = diff = missing = 0
    for s in sessions:
        for name in os.listdir(os.path.join(a_root, "tiles", s)):
            p, q = os.path.join(a_root, "tiles", s, name), os.path.join(ref, "tiles", s, name)
            if not os.path.exists(q):
                missing += 1
                continue
            with open(p, "rb") as fp, open(q, "rb") as fq:
                if hashlib.sha256(fp.read()).digest() == hashlib.sha256(fq.read()).digest():
                    same += 1
                else:
                    diff += 1
    print(f"A against the reference export: {same:,} tiles the same to the byte, {diff:,} different, {missing:,} not in the reference")

inj_a, inj_b = injections(a_root), injections(b_root)
print("per session: cells (A, B, shared); target and inputs, B less A in A's noise (rms over a cell: median cell / worst cell;")
print("pixels moved by over 0.5 sigma); noise plane B over A (median cell, range); stars: draws with the same count,")
print("stars at the same place, and the 10th / 50th / 90th percentile of amplitude B over A at those, under the noise")
print("and at 5 times the noise and over")
for s in sessions:
    da, db = os.path.join(a_root, "tiles", s), os.path.join(b_root, "tiles", s)
    cells_a = {n[:-len("_master.f16")] for n in os.listdir(da) if n.endswith("_master.f16")}
    cells_b = {n[:-len("_master.f16")] for n in os.listdir(db) if n.endswith("_master.f16")}
    shared = sorted(cells_a & cells_b)
    t_rms, t_big, i_rms, i_big, n_ratio = [], [], [], [], []
    same_count = draws = 0
    same_place = stars = 0
    faint, bright = [], []
    for c in shared:
        sig = np.maximum(tile(os.path.join(da, c + "_master.sigma.f16")), 1e-12)
        ta, tb = tile(os.path.join(da, c + "_master.f16")), tile(os.path.join(db, c + "_master.f16"))
        d = (tb - ta) / sig
        t_rms.append(float(np.sqrt(np.mean(d * d))))
        t_big.append(float(np.mean(np.abs(d) > 0.5)))
        n_ratio.append(float(np.median(tile(os.path.join(db, c + "_master.sigma.f16")) / sig)))
        for k in range(4):
            name = f"{c}_deg{k:03d}.f16"
            sa = np.maximum(tile(os.path.join(da, name[:-4] + ".sigma.f16")), 1e-12)
            d = (tile(os.path.join(db, name)) - tile(os.path.join(da, name))) / sa
            i_rms.append(float(np.sqrt(np.mean(d * d))))
            i_big.append(float(np.mean(np.abs(d) > 0.5)))
            key = f"tiles/{s}/{name}"
            ra, rb = inj_a.get(key), inj_b.get(key)
            if ra is None or rb is None:
                continue
            draws += 1
            same_count += ra["Placed"] == rb["Placed"]
            noise = float(np.median(sa))
            pa = {(round(st["X"], 2), round(st["Y"], 2)): st for st in ra["Stars"]}
            for st in rb["Stars"]:
                stars += 1
                m = pa.get((round(st["X"], 2), round(st["Y"], 2)))
                if m is not None:
                    same_place += 1
                    if m["Amplitude"][0] > 0:
                        over = m["Amplitude"][0] / noise
                        if over < 1:
                            faint.append(st["Amplitude"][0] / m["Amplitude"][0])
                        elif over >= 5:
                            bright.append(st["Amplitude"][0] / m["Amplitude"][0])
    q = lambda v: f"{np.median(v):.3f} / {np.max(v):.3f}" if v else "-"
    p = lambda v: (f"{len(v):,} stars, " + " / ".join(f"{x:.3f}" for x in np.percentile(v, [10, 50, 90]))) if v else "none"
    print(f"{s[:70]}")
    print(f"  cells {len(cells_a)}, {len(cells_b)}, {len(shared)}; target {q(t_rms)}, moved {np.mean(t_big) * 100 if t_big else 0:.2f} %; "
          f"inputs {q(i_rms)}, moved {np.mean(i_big) * 100 if i_big else 0:.2f} %; noise {np.median(n_ratio) if n_ratio else float('nan'):.4f} "
          f"({np.min(n_ratio) if n_ratio else float('nan'):.4f} to {np.max(n_ratio) if n_ratio else float('nan'):.4f})")
    print(f"  stars: same count in {same_count} of {draws} draws; {same_place:,} of {stars:,} at the same place "
          f"({same_place / max(stars, 1) * 100:.1f} %); amplitude under the noise: {p(faint)}; at 5 and over: {p(bright)}")
    if plates:
        la, lb = star_rows(plates[0], s), star_rows(plates[1], s)
        print(f"  star lists: {len(la):,} and {len(lb):,} rows; not in the other list {len(la - lb):,} and {len(lb - la):,}")
