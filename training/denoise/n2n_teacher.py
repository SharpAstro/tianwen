"""R2p's teacher (docs/plans/star-remover-training.md, "R2p: one model distilled from E5's average, pre-registered"): run an
average of star-remover checkpoints over every injected draw of a training cache, each model through each of the tile's
views and turned back, exactly as r2_infer.py averages an ensemble, and write the outputs as teacher.f16 in the cache's own
cell and draw order, with teacher.json saying what made them. n2n_smoke.py --train --teacher <out> regresses each draw onto
its row instead of the clean tile.

    python n2n_teacher.py --cache C:/temp/tianwen-scratch/r2-synth4 --ckpt r2i_synth4_s0.pt,r2i_synth4_s1.pt --tta 8 \
        --out C:/temp/tianwen-scratch/r2-synth4-teacher-e5

The output is resumable: it is written as a memmap in blocks of cells, and a block whose cells are already written (said by
teacher.done, the count of cells finished) is not run again.
"""
import argparse
import hashlib
import json
import os
import time

import numpy as np

import n2n_smoke as S


def main():
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("--cache", required=True, help="the training cache (tiles.f16, sigma.f16, meta.json) and its checkpoints")
    p.add_argument("--ckpt", required=True, help="the members' checkpoint names in --cache, comma separated")
    p.add_argument("--tta", type=int, choices=(1, 2, 4, 8), default=8,
                   help="each member's views, r2_infer.py's: 1 the tile as it is, 2 a mirror, 4 the turns, 8 all")
    p.add_argument("--out", required=True, help="the teacher folder (created)")
    p.add_argument("--batch", type=int, default=16)
    p.add_argument("--block", type=int, default=64, help="cells per resumable block")
    a = p.parse_args()

    import torch
    dev = "cuda" if torch.cuda.is_available() else "cpu"
    mm, meta = S.open_cache(a.cache)
    planes, has = S.open_sigma(a.cache, meta)
    cells, draws = meta["cells"], int(meta.get("draws", S.SUBS_PER_CELL))
    if not meta.get("injected"):
        raise SystemExit(f"{a.cache} holds real subs, not injected draws: a teacher's target needs a synthetic cache")
    names = [c.strip() for c in a.ckpt.split(",")]
    models = [S.load_model(a.cache, c, dev)[0] for c in names]
    if not all(m.cond_map for m in models):
        raise SystemExit("every member must take the stored noise plane (--cond-map), as the line's models do")
    if planes is None or not has[:, 1:draws + 1].all():
        raise SystemExit(f"{a.cache} has draws with no stored noise plane")
    views = [(flip, turns) for flip in (False, True) for turns in range(4)]
    views = {1: views[:1], 2: [views[0], views[4]], 4: views[:4], 8: views}[a.tta]

    os.makedirs(a.out, exist_ok=True)
    with open(os.path.join(a.cache, "meta.json"), "rb") as fh:
        cache_sha = hashlib.sha256(fh.read()).hexdigest()
    members = []
    for c in names:
        with open(os.path.join(a.cache, c), "rb") as fh:
            members.append({"name": c, "sha256": hashlib.sha256(fh.read()).hexdigest()})
    info = {"cells": cells, "draws": draws, "members": names, "member_sha256": [m["sha256"] for m in members],
            "views": len(views), "cache": os.path.abspath(a.cache), "cache_meta_sha256": cache_sha,
            "layout": "float16 [cells, draws, C, TILE, TILE], draw j is the cache's sub slot j + 1"}
    # Written FIRST, so a resumed run can only continue the teacher it started; the trainer reads teacher.done to know
    # every cell is through.
    info_path = os.path.join(a.out, "teacher.json")
    if os.path.exists(info_path):
        with open(info_path, encoding="utf-8") as fh:
            old = json.load(fh)
        if old != info:
            raise SystemExit(f"{a.out} holds another teacher ({old.get('members')} over {old.get('views')} views); move it aside")
    else:
        with open(info_path, "w", encoding="utf-8", newline="\n") as fh:
            json.dump(info, fh, indent=1)
    path = os.path.join(a.out, "teacher.f16")
    out = np.memmap(path, dtype=np.float16, mode="r+" if os.path.exists(path) else "w+",
                    shape=(cells, draws, S.CH, S.TILE, S.TILE))
    done_path = os.path.join(a.out, "teacher.done")
    start = int(open(done_path).read()) if os.path.exists(done_path) else 0
    print(f"teacher: {' + '.join(names)} over {len(views)} views, {cells} cells x {draws} draws, from cell {start}", flush=True)

    t0 = time.perf_counter()
    with torch.no_grad():
        for c0 in range(start, cells, a.block):
            c1 = min(cells, c0 + a.block)
            pairs = [(c, d) for c in range(c0, c1) for d in range(draws)]
            for i in range(0, len(pairs), a.batch):
                chunk = pairs[i:i + a.batch]
                ci = np.array([c for c, _ in chunk])
                si = np.array([d + 1 for _, d in chunk])
                x = torch.from_numpy(np.ascontiguousarray(mm[ci, si])).to(dev).float()
                inp = S.with_plane(x, planes[ci, si])
                # Each member through each view, turned back and averaged: r2_infer.py's loop, so the teacher IS the
                # average the line scored.
                total = None
                for model in models:
                    for flip, turns in views:
                        v = inp.flip(-1) if flip else inp
                        o = torch.rot90(model(torch.rot90(v, turns, dims=(-2, -1))), -turns, dims=(-2, -1))
                        o = o.flip(-1) if flip else o
                        total = o if total is None else total + o
                y = (total / (len(models) * len(views))).cpu().numpy().astype(np.float16)
                for (c, d), t in zip(chunk, y):
                    out[c, d] = t
            out.flush()
            # Written only once the block is flushed, so a run taken away mid-block redoes that block and no more.
            with open(done_path + ".tmp", "w") as fh:
                fh.write(str(c1))
            os.replace(done_path + ".tmp", done_path)
            if c0 == start or (c1 // a.block) % 10 == 0 or c1 == cells:
                rate = (c1 - start) * draws / max(time.perf_counter() - t0, 1e-9)
                print(f"  {c1}/{cells} cells, {rate:.1f} draws/s", flush=True)
    print(f"written {path}: {cells} cells x {draws} draws", flush=True)


if __name__ == "__main__":
    main()
