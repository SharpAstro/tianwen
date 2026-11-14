"""A training cache whose every cell is taken whole from one of two prepared caches, at even odds (R3u, 2026-11-14).

    python -I mix_caches.py <cache a> <cache b> <out> [--seed 0]

The two caches must be prepared from exports that differ only in a draw (the same cells in the same order, the same
star tables and noise-plane flags); R3u's are synth22 (channel spread 3) and synth23 (spread 2). A cell's tiles and noise
planes are copied byte for byte from the one source its draw names, so every cell of the mix is a cell an export made.
Writes tiles.f16, sigma.f16, the files the two share, and mix.json (the seed, the sources and each cell's source).
Glue only: nothing is computed from the pixels. A stop file <out>/mix.stop ends the copy between blocks.
"""
import json, os, shutil, sys
import numpy as np

SHARED = ["meta.json", "stars.json", "stars.npy", "empty.npy", "sigma_has.npy"]
BLOCK = 64  # cells a write


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    seed = int(sys.argv[sys.argv.index("--seed") + 1]) if "--seed" in sys.argv else 0
    if len(args) != 3:
        raise SystemExit(__doc__)
    a, b, out = args
    for f in SHARED:
        if open(f"{a}/{f}", "rb").read() != open(f"{b}/{f}", "rb").read():
            raise SystemExit(f"{f} differs between the two caches: they are not the same cells")
    cells = json.load(open(f"{a}/meta.json"))["cells"]
    os.makedirs(out, exist_ok=True)
    pick = np.random.default_rng(seed).permutation(cells) < cells // 2  # True: the cell is b's; exactly half of them
    for f in SHARED:
        shutil.copyfile(f"{a}/{f}", f"{out}/{f}")
    for f in ["tiles.f16", "sigma.f16"]:
        size = os.path.getsize(f"{a}/{f}")
        if size != os.path.getsize(f"{b}/{f}") or size % cells:
            raise SystemExit(f"{f}: the two caches' sizes differ or do not divide into {cells} cells")
        per = size // cells
        with open(f"{a}/{f}", "rb") as fa, open(f"{b}/{f}", "rb") as fb, open(f"{out}/{f}.partial", "wb") as fo:
            for start in range(0, cells, BLOCK):
                if os.path.exists(f"{out}/mix.stop"):
                    raise SystemExit("stopped by mix.stop")
                for i in range(start, min(start + BLOCK, cells)):
                    src = fb if pick[i] else fa
                    src.seek(i * per)
                    fo.write(src.read(per))
        os.replace(f"{out}/{f}.partial", f"{out}/{f}")
        print(f"{f}: {cells} cells of {per} bytes", flush=True)
    json.dump({"seed": seed, "a": a, "b": b, "from_b": [int(i) for i in np.flatnonzero(pick)]}, open(f"{out}/mix.json", "w"))
    print(f"mixed: {int(pick.sum())} cells from {b}, {cells - int(pick.sum())} from {a}")


if __name__ == "__main__":
    main()
