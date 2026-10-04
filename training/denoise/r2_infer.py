"""R2's inference half (docs/plans/star-remover-training.md, section 5): run a star-remover checkpoint over a Stars-mode
export's val draws and write each output beside an outputs.jsonl, which `tianwen dataset starless-eval` scores. The
measures are C#'s (StarRemovalEval); this only runs the net, the way the trainer's held-out pass does (the stored
per-pixel noise plane appended for a --cond-map checkpoint).

    python r2_infer.py --export C:/temp/tianwen-scratch/r1-train/random --cache C:/temp/tianwen-scratch/r2-random \
        --ckpt r2_random_s0.pt --val-from-list arms/r2-val.txt --out C:/temp/e2/r2-eval/random_s0
    tianwen dataset starless-eval --export C:/temp/tianwen-scratch/r1-train/random --outputs C:/temp/e2/r2-eval/random_s0
"""
import argparse
import json
import os

import numpy as np

import n2n_smoke as S

SIGMA_EXT = ".sigma.f16"


def main():
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("--export", required=True, help="the Stars-mode export (injections.jsonl and its tiles)")
    p.add_argument("--cache", required=True, help="where the checkpoint lives")
    p.add_argument("--ckpt", required=True, help="the checkpoint's file name in --cache")
    p.add_argument("--val-from-list", required=True, help="the sessions to score, one id per line (# comments)")
    p.add_argument("--out", required=True, help="the outputs directory (created)")
    p.add_argument("--batch", type=int, default=16)
    a = p.parse_args()

    import torch
    dev = "cuda" if torch.cuda.is_available() else "cpu"
    sessions = set(S.read_name_list(a.val_from_list, "val"))
    rows = []
    with open(os.path.join(a.export, "injections.jsonl"), encoding="utf-8") as fh:
        for line in fh:
            if line.strip():
                r = json.loads(line)
                if r["SessionId"] in sessions:
                    rows.append(r)
    if not rows:
        raise SystemExit(f"no draw of {a.export} belongs to a session in {a.val_from_list}")

    model, _ = S.load_model(a.cache, a.ckpt, dev)
    tile_bytes = S.CH * S.TILE * S.TILE * 2
    os.makedirs(os.path.join(a.out, "tiles"), exist_ok=True)
    index = []
    with torch.no_grad():
        for i in range(0, len(rows), a.batch):
            chunk = rows[i:i + a.batch]
            x = np.stack([read(os.path.join(a.export, r["Tile"]), tile_bytes, (S.CH, S.TILE, S.TILE)) for r in chunk])
            xt = torch.from_numpy(x).to(dev)
            if model.cond_map:
                planes = np.stack([read(os.path.join(a.export, r["Tile"][:-len(".f16")] + SIGMA_EXT), S.TILE * S.TILE * 2,
                                        (S.TILE, S.TILE)) for r in chunk])
                y = model(S.with_plane(xt, planes)).cpu().numpy()
            else:
                y = model(xt).cpu().numpy()
            for r, out in zip(chunk, y):
                name = f"tiles/{len(index):06d}.f16"
                out.astype("<f2").tofile(os.path.join(a.out, name))
                index.append({"Tile": r["Tile"], "Output": name})
    with open(os.path.join(a.out, "outputs.jsonl"), "w", encoding="utf-8", newline="\n") as fh:
        for row in index:
            fh.write(json.dumps(row) + "\n")
    print(f"{len(index)} draws of {len(sessions)} sessions through {a.ckpt} -> {a.out}")


def read(path, nbytes, shape):
    raw = open(path, "rb").read()
    if len(raw) != nbytes:
        raise SystemExit(f"{path} is {len(raw)} bytes, expected {nbytes}")
    return np.frombuffer(raw, "<f2").reshape(shape).astype(np.float32)


if __name__ == "__main__":
    main()
