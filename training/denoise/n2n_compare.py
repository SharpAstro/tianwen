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
    ap.add_argument("--only", default=None, help="keep the cells of sessions whose id contains one of these "
                                                 "comma-separated strings (n2n_starsplit.py's --only)")
    ap.add_argument("--pick", choices=("random", "bright"), default="random",
                    help="bright: the cells with the most of half B's low-pass at 0.45 or above, the levels where "
                         "a denoiser loses filaments; random: a seeded draw")
    ap.add_argument("--plane-factor", type=float, default=1.0,
                    help="scale the estimated half-A planes a --cond-map checkpoint is given, e.g. by the factor "
                         "n2n_starsplit.py --plane-truth-anchor printed for the session (the ORACLE condition); "
                         "1 is the estimate as the product would compute it")
    ap.add_argument("--input", default="half_a",
                    help="what the models are given: half_a (default), or subN for sub slot N (0 to 7), ONE calibrated "
                         "and registered exposure, pre-stretched on its own like any input. A sub's own stretch is not "
                         "retained and it has no plane of its own, so a --cond-map checkpoint gets the MASTER's plane "
                         "scaled to the sub's own sky noise (sub minus master over the sky; the master holds the sub "
                         "at 1/N, which this ignores). An approximation, printed with its factor")
    a = ap.parse_args()

    import torch
    dev = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    mm, meta = S.open_cache(a.cache)
    if meta.get("slots", S.SLOTS_SUBS_ONLY) <= S.SLOT_HALF_B:
        raise SystemExit(f"{a.cache} has no half-master slots; this needs a bake that exports halves")

    halves = meta["has_halves"]
    val = [i for i in range(meta["train_cells"], meta["cells"]) if halves[i]]
    if a.only:
        wanted = [t.strip().lower() for t in a.only.split(",") if t.strip()]
        val = [i for i in val if any(t in meta["keys"][i][0].lower() for t in wanted)]
    if not val:
        raise SystemExit("no val cell carries a half-master pair")
    if a.pick == "bright":
        from scipy.ndimage import gaussian_filter
        frac = []
        for i in val:
            lb = S.crop(np.asarray(mm[i:i + 1, S.SLOT_HALF_B], dtype=np.float32))[0].mean(axis=0)
            frac.append(float((gaussian_filter(lb, 2.0) >= 0.45).mean()))
        order = np.argsort(frac)[::-1][:a.cells]
        picked = sorted(val[k] for k in order)
        print(f"{len(picked)} brightest cells of {len(val)}: {picked} (fraction at 0.45 or above: "
              + ", ".join(f"{frac[k]:.2f}" for k in sorted(order, key=lambda k: val[k])) + ")")
    else:
        rng = np.random.default_rng(a.seed)
        picked = sorted(rng.choice(val, size=min(a.cells, len(val)), replace=False).tolist())
        print(f"{len(picked)} cells of {len(val)} that carry a pair: {picked}")

    if a.input == "half_a":
        slot, in_label = S.SLOT_HALF_A, "raw half A"
    elif a.input.startswith("sub") and a.input[3:].isdigit() and int(a.input[3:]) < S.SUBS_PER_CELL:
        slot, in_label = 1 + int(a.input[3:]), f"raw sub {a.input[3:]}"
        if not meta.get("has_subs", False):
            raise SystemExit(f"{a.cache} holds no sub tiles")
    else:
        raise SystemExit(f"--input {a.input!r}: half_a or sub0 to sub{S.SUBS_PER_CELL - 1}")
    src = np.asarray(mm[picked, slot], dtype=np.float32)
    half_b = np.asarray(mm[picked, S.SLOT_HALF_B], dtype=np.float32)
    master = np.asarray(mm[picked, S.SLOT_MASTER], dtype=np.float32)
    # A --cond-map checkpoint needs the input's per-pixel planes (S.denoise refuses one without them).
    sig, sig_has = S.open_sigma(a.cache, meta)
    planes = None
    if sig is not None and slot == S.SLOT_HALF_A and sig_has[picked, slot].all():
        planes = np.asarray(sig[picked, slot], dtype=np.float32)
    elif sig is not None and slot != S.SLOT_HALF_A and sig_has[picked, S.SLOT_MASTER].all():
        # The anchor is read over EVERY kept cell (all of --only's sessions), not just the picked ones: the
        # brightest cells of a nebula field can hold no sky at all.
        from scipy.ndimage import gaussian_filter
        lum_in = S.crop(np.asarray(mm[val, slot], dtype=np.float32)).mean(axis=1)
        lum_m = S.crop(np.asarray(mm[val, S.SLOT_MASTER], dtype=np.float32)).mean(axis=1)
        lvl = np.stack([gaussian_filter(t, 2.0) for t in lum_m])
        sky = (lvl >= 0.15) & (lvl < 0.30)
        if sky.sum() < 3000:
            raise SystemExit(f"under 3000 sky pixels in {len(val)} cells, so no sub anchor")
        d = (lum_in - lum_m)[sky]
        truth = 1.4826 * float(np.median(np.abs(d - np.median(d))))
        est = float(np.mean(S.crop(np.asarray(sig[val, S.SLOT_MASTER], dtype=np.float32))[sky])) / S.PLANE_SCALE
        planes = np.asarray(sig[picked, S.SLOT_MASTER], dtype=np.float32) * (truth / est)
        print(f"sub plane: the master's scaled x{truth / est:.2f} to the sub's sky noise {truth:.4f} "
              f"({int(sky.sum())} sky px over {len(val)} cells)")

    columns = [(in_label, S.crop(src))]
    for spec in a.models:
        slug, ckpt = spec.split("=", 1)
        # "slug=ckpt.pt@0.6" blends the output back toward the input at 0.6, which is the shipped
        # strength dial. Without it a comparison is at each model's OWN strength, and a model that
        # simply acts harder looks different for that reason alone -- which is how a colour cast and
        # a denoising difference become impossible to tell apart by eye.
        alpha = 1.0
        if "@" in ckpt:
            ckpt, a_s = ckpt.rsplit("@", 1)
            alpha = float(a_s)
        # "slug=ckpt.pt*0.53" gives that model its own plane factor, so the estimate and the oracle
        # anchor of one --cond-map checkpoint can stand side by side.
        factor = a.plane_factor
        if "*" in ckpt:
            ckpt, f_s = ckpt.rsplit("*", 1)
            factor = float(f_s)
        raw_in = S.crop(src)
        path = ckpt if os.path.isabs(ckpt) else os.path.join(a.cache, ckpt)
        cond_map = bool(torch.load(path, map_location="cpu").get("cond_map", False))
        if cond_map and planes is None:
            raise SystemExit(f"{slug} conditions on a per-pixel plane and {a.cache} holds none for these cells")
        out = S.crop(S.denoise(a.cache, ckpt, src, dev,
                               planes=planes * factor if cond_map else None))
        if cond_map and factor != 1.0:
            slug = f"{slug} plane x{factor:.2f}"
        if alpha != 1.0:
            out = raw_in + alpha * (out - raw_in)
            slug = f"{slug}@{alpha:g}"
        columns.append((slug, out))
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
