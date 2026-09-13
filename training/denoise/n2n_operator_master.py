"""Deconvolve a WHOLE master with the torch operator, in tiles, and write it back as linear FITS.

`n2n_operator_real.py` reads one crop against a truth; this is the same operator run over a full
master for looking at, with no truth in hand: the master's own stretch parameters (the exporter's
rule for a session), a frame-wide kernel per channel (the estimator's whole-frame fallback), and the
runtime rule E3.3's kill line fixed for the learned prior, i.e. the frame resampled by 1.28 so its
stars sit inside the prior's width band, deconvolved, and brought back to native scale.

Tiles overlap by a margin that is cut from every output tile (replicate padding at the frame's edge),
so the only seam left is Richardson-Lucy's own replicate-padded border, 96 px in from every tile
edge, which the margin covers. Each channel is deconvolved in its own pass with its own kernel and
its plane kept, as the readout does.

    python n2n_operator_master.py --cache C:/temp/tianwen-scratch/n2n-p2-blur-clamped \
        --input soft.fits --out-dir C:/temp/e2/matrix/asis --arms input,e30,prior
"""
import argparse
import os
import time

import numpy as np
import torch
from astropy.io import fits

import n2n_deconv_gate as DG
import n2n_operator as OP
import n2n_smoke as S


def stretch_np(unit, mins, betas):
    out = np.empty_like(unit, dtype=np.float32)
    for c in range(unit.shape[0]):
        shifted = np.clip(unit[c] - mins[c], 0.0, None).astype(np.float64)
        out[c] = OP.mtf(betas[c], shifted).astype(np.float32)
    return out


def unstretch_np(stretched, mins, betas):
    out = np.empty_like(stretched, dtype=np.float32)
    for c in range(stretched.shape[0]):
        y = np.clip(stretched[c], 0.0, 1.0).astype(np.float64)
        out[c] = (OP.mtf(1.0 - betas[c], y) + mins[c]).astype(np.float32)
    return out


def to_png(stretched, path):
    from PIL import Image as PILImage
    rgb = (np.clip(stretched, 0.0, 1.0) * 255.0 + 0.5).astype(np.uint8).transpose(1, 2, 0)
    PILImage.fromarray(rgb, "RGB").save(path, optimize=True)


def run_tiled(model, stretched, labels, kernels, beta, tile, margin, dev, name):
    """The operator over the whole stretched frame [3, H, W], one kernel per channel."""
    _, h, w = stretched.shape
    core = tile - 2 * margin
    ny, nx = -(-h // core), -(-w // core)
    ph, pw = ny * core + 2 * margin, nx * core + 2 * margin
    padded = np.pad(stretched, ((0, 0), (margin, ph - h - margin), (margin, pw - w - margin)), mode="edge")
    out = np.zeros((3, ny * core, nx * core), dtype=np.float32)
    t0 = time.perf_counter()
    with torch.no_grad():
        for i in range(ny):
            for j in range(nx):
                y0, x0 = i * core, j * core
                x = torch.from_numpy(padded[None, :, y0:y0 + tile, x0:x0 + tile]).to(dev)
                for c, fwhm in enumerate(kernels):
                    lab = labels.copy()
                    lab[0, OP.LABEL_KERNEL_FWHM] = fwhm
                    lab[0, OP.LABEL_KERNEL_BETA] = beta
                    y = model(DG.with_psf01(x, lab))[0, c, margin:margin + core, margin:margin + core]
                    out[c, y0:y0 + core, x0:x0 + core] = y.cpu().numpy()
            print(f"  {name}: row {i + 1}/{ny}, {time.perf_counter() - t0:.0f} s", flush=True)
    return out[:, :h, :w]


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--cache", required=True)
    p.add_argument("--input", required=True, help="a linear master FITS (3 planes)")
    p.add_argument("--out-dir", required=True)
    p.add_argument("--arms", default="input,e30,prior", help="any of input, e30, prior")
    p.add_argument("--checkpoint", default="e32_s0_final.pt")
    p.add_argument("--kernels", default="0.77,0.91,0.98", help="frame-wide kernel FWHM px per channel")
    p.add_argument("--beta", type=float, default=4.0)
    p.add_argument("--rl-k", type=int, default=20)
    p.add_argument("--zoom", type=float, default=1.28125, help="the prior's scale (E3.2's round trip)")
    p.add_argument("--tile", type=int, default=1024)
    p.add_argument("--margin", type=int, default=96)
    p.add_argument("--device", default="cuda")
    args = p.parse_args()
    arms = [a.strip() for a in args.arms.split(",") if a.strip()]
    os.makedirs(args.out_dir, exist_ok=True)
    dev = torch.device(args.device)

    with fits.open(args.input, memmap=False) as h:
        hdu = next(x for x in h if x.data is not None)
        data = np.asarray(hdu.data, dtype=np.float32)
        header = hdu.header.copy()
    if data.ndim != 3 or data.shape[0] != 3:
        raise SystemExit(f"expected a 3-plane master, got {data.shape}")
    data = np.nan_to_num(data, nan=0.0)
    data_max = float(data.max())
    divisor = data_max if data_max > 1.0 else 1.0
    mins, betas = OP.session_stretch_params(data, divisor)
    inv = np.float32(1.0 / divisor) if divisor != 1.0 else np.float32(1.0)
    unit = data * inv
    stretched = stretch_np(unit, mins, betas)
    print(f"{args.input}: {data.shape[2]}x{data.shape[1]}, divisor {divisor:.4g}, "
          f"beta ({betas[0]:.4f}, {betas[1]:.4f}, {betas[2]:.4f}), kernels {args.kernels} px at beta {args.beta}, K={args.rl_k}")

    labels = np.zeros((1, OP.LABEL_COUNT), dtype=np.float32)
    labels[0, OP.LABEL_MIN] = mins
    labels[0, OP.LABEL_BETA] = betas
    labels[0, OP.LABEL_PSF01] = 0.5
    kernels = [float(v) for v in args.kernels.split(",")]

    def write(name, stretched_out):
        to_png(stretched_out, os.path.join(args.out_dir, f"{name}.png"))
        if name != "input":
            lin = unstretch_np(stretched_out, mins, betas) * np.float32(divisor)
            fits.PrimaryHDU(lin.astype(np.float32), header=header).writeto(
                os.path.join(args.out_dir, f"{name}.fits"), overwrite=True)
        print(f"  wrote {name}", flush=True)

    if "input" in arms:
        write("input", stretched)
    if "e30" in arms:
        model = OP.RLOperator(args.rl_k).to(dev).eval()
        write("e30", run_tiled(model, stretched, labels, kernels, args.beta, args.tile, args.margin, dev, "E3.0"))
    if "prior" in arms:
        from scipy.ndimage import zoom as ndzoom
        model, _ = S.load_model(args.cache, args.checkpoint, dev)
        model = model.eval()
        _, h, w = unit.shape
        zoomed = np.stack([ndzoom(unit[c], args.zoom, order=3) for c in range(3)]).astype(np.float32)
        hz, wz = zoomed.shape[1:]
        print(f"  prior at {args.zoom}: {wz}x{hz}", flush=True)
        out_z = run_tiled(model, stretch_np(zoomed, mins, betas), labels, [k * args.zoom for k in kernels],
                          args.beta, int(round(args.tile * args.zoom / 16)) * 16, args.margin, dev,
                          f"prior {args.checkpoint}")
        back = np.stack([ndzoom(out_z[c], (h / hz, w / wz), order=3) for c in range(3)]).astype(np.float32)
        if back.shape[1:] != (h, w):
            back = back[:, :h, :w]
            if back.shape[1:] != (h, w):
                raise SystemExit(f"round trip landed on {back.shape}, wanted {(3, h, w)}")
        write("prior", back)


if __name__ == "__main__":
    main()
