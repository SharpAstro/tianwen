r"""E7 (#844): the deconvolution operator's WHOLE-FRAME runtime, in Python, through its ONNX graph: the reference the C#
runner (`OperatorDeconvolutionRunner`) is held to, and the fixture its parity test reads.

It is `n2n_operator_master.py`'s prior arm with the torch module swapped for the exported graph, step for step:
the stretch's minimum and balance once on the native frame; the LINEAR frame up by the resample factor
(`scipy.ndimage.zoom(order=3)`); stretched with the native parameters; each channel's Moffat kernel at its FWHM times
the factor; overlapping tiles with a 96 px margin cut from each, the frame's edge replicated past it; the output down
to the native size in STRETCHED units; unstretched. Its one difference is the stretch's minimum: the covered pixels'
(NaN skipped), as `n2n_operator_real.py` (which produced E3.4d's published row) and every C# runner take it, where the
master runner turned NaN into 0 first; on a frame with no canvas ring the two agree. And both zooms are scipy's
`mode='mirror'`, the readouts' default `'constant'` to the bit except on the frame sizes where the last output
coordinate rounds a hair past the input's last sample and `'constant'` zeroes that whole row or column, a rounding
artefact the runtime does not reproduce (`SplineZoom`'s remarks).

    python n2n_operator_runtime.py --fixture --graph C:/temp/e2/e7-export/tianwen_deconv_operator_fixture.onnx \
        --out ../../src/TianWen.Lib.Tests/Data/Deconv/operator_runtime_fixture.json.gz --height 61 --width 71 --tile 224
    python n2n_operator_runtime.py --fixture --graph ../../src/TianWen.AI.Imaging/models/tianwen_deconv_operator_e34d_s0.onnx \
        --out ../../src/TianWen.Lib.Tests/Data/Deconv/operator_runtime_e34d.json.gz --height 53 --width 61 --tile 256
"""
import argparse
import base64
import gzip
import io
import json
import os
import sys

import numpy as np

import n2n_operator as OP
import n2n_operator_export as EX

MARGIN = 96


def tile_for(resample, native_tile=1024):
    """The master runner's tile: round(1024 * factor / 16) * 16 (1312 at 1.28125)."""
    return int(round(native_tile * resample / 16)) * 16


def deconvolve(sess, unit, fwhms, beta, resample, tile=None, margin=MARGIN):
    """The whole-frame runtime on a LINEAR unit frame [3, H, W]: (output [3, H, W] linear, mins, betas, (hz, wz), k)."""
    from scipy.ndimage import zoom as ndzoom

    _, h, w = unit.shape
    tile = tile or tile_for(resample)
    mins, betas = OP.session_stretch_params(unit, 1.0)
    zoomed = np.stack([ndzoom(unit[c], resample, order=3, mode="mirror") for c in range(3)]).astype(np.float32)
    hz, wz = zoomed.shape[1:]
    stretched = EX.stretch(zoomed, mins, betas)
    kernel = EX.channel_kernels([f * resample for f in fwhms], beta)

    core = tile - 2 * margin
    ny, nx = -(-hz // core), -(-wz // core)
    ph, pw = ny * core + 2 * margin, nx * core + 2 * margin
    padded = np.pad(stretched, ((0, 0), (margin, ph - hz - margin), (margin, pw - wz - margin)), mode="edge")
    out = np.zeros((3, ny * core, nx * core), dtype=np.float32)
    for i in range(ny):
        for j in range(nx):
            y0, x0 = i * core, j * core
            y = EX.run_graph(sess, padded[None, :, y0:y0 + tile, x0:x0 + tile], kernel, mins, betas)[0]
            out[:, y0:y0 + core, x0:x0 + core] = y[:, margin:margin + core, margin:margin + core]
    out = out[:, :hz, :wz]
    back = np.stack([ndzoom(out[c], (h / hz, w / wz), order=3, mode="mirror") for c in range(3)]).astype(np.float32)
    if back.shape[1:] != (h, w):
        raise SystemExit(f"the round trip landed on {back.shape}, wanted {(3, h, w)}")
    return EX.unstretch(back, mins, betas), mins, betas, (hz, wz), kernel.shape[-1]


def synthetic_frame(seed, h, w):
    """A linear RGB frame with a sky gradient, its noise and Moffat stars, in [0, 1]."""
    rng = np.random.default_rng(seed)
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float64)
    lin = (0.02 + 0.01 * xx / w + 0.005 * yy / h)[None] * np.array([1.0, 1.1, 1.3])[:, None, None]
    lin = lin + rng.normal(0.0, 0.002, (3, h, w))
    for _ in range(max(4, h * w // 600)):
        cy, cx = rng.uniform(0, h), rng.uniform(0, w)
        amp = rng.uniform(0.02, 0.7)
        alpha = rng.uniform(1.0, 2.2)
        star = amp * (1.0 + ((yy - cy) ** 2 + (xx - cx) ** 2) / alpha ** 2) ** -3.0
        lin = lin + star[None] * np.array([1.0, 0.92, 0.8])[:, None, None]
    return np.clip(lin, 0.0, 1.0).astype(np.float32)


def f32(array):
    """A float32 array as base64 of its little-endian bytes, row-major [3, H, W]: a third of a decimal list's size."""
    return base64.b64encode(np.ascontiguousarray(array, dtype="<f4").tobytes()).decode("ascii")


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--fixture", action="store_true", help="write a synthetic frame and the runtime's answer for it")
    p.add_argument("--graph", required=True, help="the operator graph (n2n_operator_export.py)")
    p.add_argument("--out", required=True, help="the fixture to write (.json.gz)")
    p.add_argument("--height", type=int, default=61, help="odd, so the stretch's median is one pixel on both sides")
    p.add_argument("--width", type=int, default=71)
    p.add_argument("--tile", type=int, default=224, help="the graph's tile; the runtime's default is tile_for(resample)")
    p.add_argument("--fwhm", default="0.77,0.91,0.98")
    p.add_argument("--beta", type=float, default=4.0)
    p.add_argument("--resample", type=float, default=1.28125)
    p.add_argument("--seed", type=int, default=844)
    args = p.parse_args()
    if not args.fixture:
        raise SystemExit("only --fixture is implemented")
    if (args.height * args.width) % 2 == 0:
        raise SystemExit("--height x --width must be odd, so the median is a pixel's in both languages")

    fwhms = [float(v) for v in args.fwhm.split(",")]
    frame = synthetic_frame(args.seed, args.height, args.width)
    sess = EX.session(args.graph)
    out, mins, betas, (hz, wz), k = deconvolve(sess, frame, fwhms, args.beta, args.resample, args.tile)
    moved = float(np.abs(out - frame).max())
    record = {
        "graph": os.path.basename(args.graph), "seed": args.seed, "height": args.height, "width": args.width,
        "fwhm": fwhms, "beta": args.beta, "resample": args.resample, "tile": args.tile, "margin": MARGIN,
        "zoomed": [hz, wz], "kernel_size": int(k),
        "stretch_min": [float(v) for v in mins], "stretch_balance": [float(v) for v in betas],
        "output_moved_max": moved,
        "frame": f32(frame), "expected": f32(out),
    }
    with gzip.open(args.out, "wt", encoding="utf-8", compresslevel=9) as f:
        json.dump(record, f)
    print(f"{args.out}: {args.width}x{args.height} -> {wz}x{hz} at {args.resample}, tile {args.tile}, kernel {k} px, "
          f"min {mins}, balance {betas}; the output moves up to {moved:.4f} from the input")
    return 0


if __name__ == "__main__":
    sys.exit(main())
