"""A labelled side-by-side of several checkpoints on the SAME cells, at 1:1.

Numbers rank models; they do not show what a model DID to a frame, and every wrong verdict in this
campaign that survived a table was caught by looking. So an arm is not finished until this has been
run and posted (`feedback_post_labelled_comparison`).

Three choices that make it honest:

  - **Half-master input, half-master reference.** The columns are: raw half A (what the model was
    given), one column per model, then half B and the master. B is the independent reference -- it
    shares no noise realisation with A -- so "did the model keep that faint star" is answerable by
    eye. The master leaks (it CONTAINS A) and is last, as context rather than truth.
  - **One stretch per ROW, taken from the raw column.** Every column of a row goes through the same
    clip, so a model that merely lifted the level cannot look cleaner. The tiles are already in the
    exporter's stretched [0,1] domain, so this is a clip and not a second stretch.
  - **1:1 pixels, centre crop.** No downscaling anywhere: a resample hides exactly the fine-scale
    smoothing and the fabricated point sources that these arms differ in.

Usage:
  python n2n_compare.py --cache <eval cache> --models white_s1=<abs.pt> control_s1=<abs.pt> \
      --cells 4 --out compare-e2.png
"""
import argparse
import os

import numpy as np

import n2n_smoke as S


def label_strip(width, height, texts, cell_width):
    """A text banner as a uint8 RGB array, drawn with PIL's default font (no font file to find)."""
    from PIL import Image as PImage, ImageDraw
    img = PImage.new("RGB", (width, height), (16, 16, 16))
    d = ImageDraw.Draw(img)
    for i, t in enumerate(texts):
        d.text((i * cell_width + 6, height // 3), t, fill=(235, 235, 235))
    return np.asarray(img)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cache", required=True, help="an eval cache carrying half-master slots")
    ap.add_argument("--models", nargs="+", required=True, help="slug=checkpoint.pt (absolute path is fine)")
    ap.add_argument("--cells", type=int, default=4)
    ap.add_argument("--out", default="compare.png")
    ap.add_argument("--seed", type=int, default=0, help="which cells are drawn, so a re-run shows the same ones")
    a = ap.parse_args()

    import torch
    dev = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    mm, meta = S.open_cache(a.cache)
    if meta.get("slots", S.SLOTS_SUBS_ONLY) <= S.SLOT_HALF_B:
        raise SystemExit(f"{a.cache} has no half-master slots; this needs a bake that exports halves")

    halves = meta["has_halves"]
    val = [i for i in range(meta["train_cells"], meta["cells"]) if halves[i]]
    if not val:
        raise SystemExit("no val cell carries a half-master pair")
    rng = np.random.default_rng(a.seed)
    picked = sorted(rng.choice(val, size=min(a.cells, len(val)), replace=False).tolist())
    print(f"{len(picked)} cells of {len(val)} that carry a pair: {picked}")

    half_a = np.asarray(mm[picked, S.SLOT_HALF_A], dtype=np.float32)
    half_b = np.asarray(mm[picked, S.SLOT_HALF_B], dtype=np.float32)
    master = np.asarray(mm[picked, S.SLOT_MASTER], dtype=np.float32)

    columns = [("raw half A", S.crop(half_a))]
    for spec in a.models:
        slug, ckpt = spec.split("=", 1)
        columns.append((slug, S.crop(S.denoise(a.cache, ckpt, half_a, dev))))
    columns.append(("half B (ref)", S.crop(half_b)))
    columns.append(("master (leaks)", S.crop(master)))

    rows = []
    for r in range(len(picked)):
        tiles = []
        for _, arr in columns:
            t = np.clip(arr[r].transpose(1, 2, 0), 0, 1)
            tiles.append((t * 255).astype(np.uint8))
        rows.append(np.concatenate(tiles, axis=1))
    body = np.concatenate(rows, axis=0)

    cell_w = rows[0].shape[1] // len(columns)
    banner = label_strip(body.shape[1], 18, [c[0] for c in columns], cell_w)
    img = np.concatenate([banner, body], axis=0)

    from PIL import Image as PImage
    out = a.out if os.path.isabs(a.out) else os.path.join(a.cache, a.out)
    PImage.fromarray(img).save(out)
    print(f"wrote {out}  ({img.shape[1]}x{img.shape[0]}, 1:1, columns: {', '.join(c[0] for c in columns)})")


if __name__ == "__main__":
    main()
