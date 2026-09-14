"""E7.2: a kernel head, the learned single-frame kernel rule (deconvolver-training.md, E7.2).

E7.1 measured that the E3.4d prior tolerates a kernel about a tenth either way of the pair's, and
that no fixed rule of a frame's own width lands in that band: the training draws' fraction reads
1.17 to 1.31 times the pair's kernel, and composing the width down to the pool's clean median
refuses the soft Statue outright because it is already sharper than that median while the pair
proves 1.19 to 1.33 of excess. Width alone cannot see excess blur; the SHAPE of the degraded tile
has to.

This head reads the kernel width from one stretched plane of a degraded tile, trained on the
operator's own cache: the tiles the prior sees (`tiles.f16`, slots 1..8 the injected draws) and the
kernel that made each (`kernels.npy` column 0, `EstimatedKernelFwhmPx`, the operator's own label),
one sample per (cell, slot, channel) because the exporter's kernel is shared across channels while
a real frame's is not, and inference wants a per-channel answer. The loss is RELATIVE (|pred - k| /
k) since the band is relative. E3.4d's scale augmentation is applied the same way (one factor per
batch, bicubic, label scaled), so the head reads the 1.28x resampled frame the prior gets. Each
plane is standardised by its own median and spread, so the head reads shape, not level.

After training the head is read on the held-out cells and on the E2.10b Statue pair exactly as
`n2n_operator_real.py` cuts it: the primary crop, the soft master's own stretch, at native scale
and at the round trip's resample, tiles of 256 px at stride 128 per channel, the median prediction
per channel against est-c (the pair's kernel, scaled with the zoom) and the same read on the SHARP
crop (what the head would ask to remove from the truth itself: the control).
"""
import argparse
import json
import os
import time

import numpy as np
import torch
import torch.nn.functional as F

import n2n_operator as OP

TILE, CH = 256, 3
SUBS_PER_CELL = 8


def load_cache(cache):
    meta = json.load(open(os.path.join(cache, "meta.json")))
    mm = np.memmap(os.path.join(cache, "tiles.f16"), dtype=np.float16, mode="r",
                   shape=(meta["cells"], meta.get("slots", SUBS_PER_CELL + 1), CH, TILE, TILE))
    kernels = np.load(os.path.join(cache, OP.KERNELS_FILE))
    return meta, mm, kernels


class KernelHead(torch.nn.Module):
    """One plane in, one kernel width out. Five stride-2 stages over a standardised 256 px tile,
    global mean and max pooled, a small MLP, softplus so the width is positive."""

    def __init__(self, width=24):
        super().__init__()
        chans = [1, width, width * 2, width * 3, width * 4, width * 6]
        layers = []
        for i in range(len(chans) - 1):
            layers += [torch.nn.Conv2d(chans[i], chans[i + 1], 3, stride=2, padding=1), torch.nn.GELU(),
                       torch.nn.Conv2d(chans[i + 1], chans[i + 1], 3, padding=1), torch.nn.GELU()]
        self.features = torch.nn.Sequential(*layers)
        self.head = torch.nn.Sequential(torch.nn.Linear(chans[-1] * 2, 96), torch.nn.GELU(), torch.nn.Linear(96, 1))

    def forward(self, x):
        f = self.features(x)
        pooled = torch.cat([f.mean(dim=(2, 3)), f.amax(dim=(2, 3))], dim=1)
        return F.softplus(self.head(pooled)).squeeze(1) + 0.05


def standardise(x):
    """Per sample: subtract the median, divide by the median absolute deviation (a robust spread the
    stars do not dominate), so a bright and a faint tile with the same star shape read alike."""
    flat = x.flatten(1)
    med = flat.median(dim=1, keepdim=True).values
    mad = (flat - med).abs().median(dim=1, keepdim=True).values * 1.4826 + 1e-6
    return ((flat - med) / mad).view_as(x)


def resample(x, factor):
    if factor == 1.0:
        return x, 1.0
    side = max(16, int(round(x.shape[-1] * factor / 16)) * 16)
    eff = side / x.shape[-1]
    return F.interpolate(x, size=(side, side), mode="bicubic", align_corners=False, antialias=True), eff


def batch_from(mm, kernels, cells, rng, batch, dev):
    """(cell, slot, channel) draws whose kernel label is finite."""
    xs, ks = [], []
    while len(xs) < batch:
        cell = int(cells[rng.integers(0, len(cells))])
        slot = int(rng.integers(0, SUBS_PER_CELL))
        k = float(kernels[cell, slot, 0])
        if not np.isfinite(k) or k <= 0:
            continue
        ch = int(rng.integers(0, CH))
        xs.append(np.asarray(mm[cell, slot + 1, ch], dtype=np.float32))
        ks.append(k)
    x = torch.from_numpy(np.stack(xs)[:, None]).to(dev)
    k = torch.tensor(ks, dtype=torch.float32, device=dev)
    # Orientation is free: a kernel's width does not change under a flip or a quarter turn.
    if rng.random() < 0.5:
        x = x.flip(-1)
    if rng.random() < 0.5:
        x = x.flip(-2)
    if rng.random() < 0.5:
        x = x.transpose(-1, -2)
    return x, k


def evaluate_cache(model, mm, kernels, cells, dev, scales, rng, per_scale=256):
    """Median relative error over held-out (cell, slot, channel) draws, per resample factor."""
    model.eval()
    out = {}
    with torch.no_grad():
        for s in scales:
            errs = []
            for _ in range(per_scale // 32):
                x, k = batch_from(mm, kernels, cells, rng, 32, dev)
                x, eff = resample(x, s)
                pred = model(standardise(x))
                errs.append(((pred - k * eff).abs() / (k * eff)).cpu().numpy())
            e = np.concatenate(errs)
            out[s] = (float(np.median(e)), float(np.percentile(e, 90)))
    model.train()
    return out


def read_pair(args):
    """The readout's crops, stretched with the soft master's own parameters (n2n_operator_real)."""
    from n2n_operator_real import read_master, stretch
    sharp, sx0, sy0 = read_master(args.sharp)
    soft, fx0, fy0 = read_master(args.soft)
    cx, cy, size = (int(v) for v in args.crop.split(","))
    dx, dy = sx0 - fx0, sy0 - fy0
    sharp_crop = sharp[:, cy:cy + size, cx:cx + size]
    soft_crop = soft[:, cy + dy:cy + dy + size, cx + dx:cx + dx + size]
    data_max = float(np.nanmax(soft))
    divisor = data_max if data_max > 1.0 else 1.0
    mins, betas = OP.session_stretch_params(soft, divisor)
    inv = np.float32(1.0 / divisor) if divisor != 1.0 else np.float32(1.0)
    return stretch(soft_crop * inv, mins, betas), stretch(sharp_crop * inv, mins, betas)


def predict_frame(model, planes, dev, zoom, tile=TILE, stride=TILE // 2):
    """Median head reading per channel over tiles of a stretched crop, resampled by `zoom` the way the
    readout resamples (side a multiple of 16, the exact factor returned)."""
    x = torch.from_numpy(np.nan_to_num(planes)[None]).to(dev).float()
    x, eff = resample(x, zoom)
    side = x.shape[-1]
    preds = []
    with torch.no_grad():
        for c in range(planes.shape[0]):
            vals = []
            tiles = []
            for y in range(0, side - tile + 1, stride):
                for xx in range(0, side - tile + 1, stride):
                    tiles.append(x[:, c:c + 1, y:y + tile, xx:xx + tile])
            for i in range(0, len(tiles), 32):
                batch = torch.cat(tiles[i:i + 32], dim=0)
                vals.append(model(standardise(batch)).cpu().numpy())
            v = np.concatenate(vals)
            preds.append((float(np.median(v)), float(np.percentile(v, 10)), float(np.percentile(v, 90)), len(v)))
    return preds, eff


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--cache", required=True)
    p.add_argument("--out", default="kernel_head_s0.pt")
    p.add_argument("--steps", type=int, default=3000)
    p.add_argument("--batch", type=int, default=32)
    p.add_argument("--lr", type=float, default=1e-3)
    p.add_argument("--width", type=int, default=24)
    p.add_argument("--scale-aug", default="0.6,1.4", help="E3.4d's straddle; 'none' for native tiles only")
    p.add_argument("--seed", type=int, default=0)
    p.add_argument("--device", default="cuda")
    p.add_argument("--sharp", default="C:/temp/e2/e210b-statue/sharp/master_StatueofLibertyNebula_light_60s_-5C_g120.fits")
    p.add_argument("--soft", default="C:/temp/e2/e210b-statue/soft/master_StatueofLibertyNebula_light_60s_-5C_g120.fits")
    p.add_argument("--crop", default="0,1024,1024")
    p.add_argument("--kernels", default="0.77,0.91,0.98", help="est-c, the pair's kernel per channel at native scale")
    p.add_argument("--zooms", default="1.0,1.285")
    p.add_argument("--eval-only", action="store_true", help="load --out from the cache and read it, no training")
    args = p.parse_args()

    torch.manual_seed(args.seed)
    rng = np.random.default_rng(args.seed)
    dev = torch.device(args.device if torch.cuda.is_available() or args.device == "cpu" else "cpu")
    meta, mm, kernels = load_cache(args.cache)
    n_train = meta["train_cells"]
    train_cells = np.arange(n_train)
    val_cells = np.arange(n_train, meta["cells"])
    finite = np.isfinite(kernels[..., 0]) & (kernels[..., 0] > 0)
    print(f"cache {args.cache}: {meta['cells']} cells ({n_train} train, {len(val_cells)} held out), "
          f"{int(finite.sum())} labelled slots; kernel label p10/50/90 "
          f"{np.percentile(kernels[..., 0][finite], [10, 50, 90]).round(3)} px; device {dev}")
    scale_aug = None if args.scale_aug == "none" else tuple(float(v) for v in args.scale_aug.split(","))

    model = KernelHead(args.width).to(dev)
    path = os.path.join(args.cache, args.out)
    if args.eval_only:
        model.load_state_dict(torch.load(path, map_location=dev))
    else:
        opt = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=1e-4)
        sched = torch.optim.lr_scheduler.OneCycleLR(opt, max_lr=args.lr, total_steps=args.steps, pct_start=0.1)
        t0 = time.perf_counter()
        running = []
        for step in range(1, args.steps + 1):
            x, k = batch_from(mm, kernels, train_cells, rng, args.batch, dev)
            eff = 1.0
            if scale_aug is not None:
                x, eff = resample(x, float(rng.uniform(*scale_aug)))
            pred = model(standardise(x))
            loss = ((pred - k * eff).abs() / (k * eff)).mean()
            opt.zero_grad(set_to_none=True)
            loss.backward()
            torch.nn.utils.clip_grad_norm_(model.parameters(), 1.0)
            opt.step()
            sched.step()
            running.append(float(loss))
            if step % 250 == 0 or step == args.steps:
                ev = evaluate_cache(model, mm, kernels, val_cells, dev, (1.0, 1.285), rng)
                print(f"step {step:5d}  train rel err {np.mean(running):.3f}  held-out median/p90: "
                      + "  ".join(f"x{s}: {m:.3f}/{p90:.3f}" for s, (m, p90) in ev.items())
                      + f"  {time.perf_counter() - t0:.0f} s", flush=True)
                running = []
        torch.save(model.state_dict(), path)
        print(f"saved {path}")

    model.eval()
    ev = evaluate_cache(model, mm, kernels, val_cells, dev, (0.75, 1.0, 1.285, 1.4), rng, per_scale=1024)
    print("\nheld-out cells, median / p90 relative error per resample factor: "
          + "  ".join(f"x{s}: {m:.3f}/{p90:.3f}" for s, (m, p90) in ev.items()))

    soft_s, sharp_s = read_pair(args)
    estc = [float(v) for v in args.kernels.split(",")]
    print(f"\nStatue pair, crop {args.crop}: head reading per channel (median over tiles, p10..p90), against est-c scaled with the zoom")
    print(f"{'zoom':>6} {'frame':>6} {'ch':>2} {'head px':>8} {'p10..p90':>14} {'est-c':>6} {'head/est-c':>10} {'tiles':>5}")
    for z in (float(v) for v in args.zooms.split(",")):
        for name, planes in (("soft", soft_s), ("sharp", sharp_s)):
            preds, eff = predict_frame(model, planes, dev, z)
            for c, (med, lo, hi, n) in enumerate(preds):
                target = estc[c] * eff
                ratio = med / target if name == "soft" else float("nan")
                print(f"{eff:6.3f} {name:>6} {c:2d} {med:8.3f} {lo:6.3f}..{hi:6.3f} {target:6.3f} {ratio:10.3f} {n:5d}")
    print("\nread: soft head/est-c inside 0.9 to 1.1 on every channel is the E7.2 pass; the sharp rows are the control, "
          "what the head would remove from the truth itself (small is right: the truth is the seeing floor, not a blurred frame).")


if __name__ == "__main__":
    main()
