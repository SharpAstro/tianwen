r"""Export a trained n2n checkpoint to ONNX, and PROVE the export reproduces torch.

The conditioning plane is the whole risk here. The model takes CH+1 channels: three image
planes plus one holding the tile's own measured background sigma, and that plane is what
makes denoising strength an input rather than a constant baked in at training time. An export
that silently drops it, or a host that recomputes it slightly differently, produces a model
that still runs and still looks like a denoiser while being fed a number it was never trained
on. So this script exports TWO shapes and measures both against torch:

  baked  -- one image input [N,3,H,W] plus a scalar `strength`; the graph computes sigma
            itself, exactly as `with_sigma` does. The host cannot get it wrong because the
            host does not do it. Requires torch.quantile to survive the export.
  plain  -- the bare net, [N,4,H,W], sigma supplied by the caller. Always exportable; moves
            the estimator into C# where it has to be re-verified.

Prefer `baked`. `plain` exists so a quantile-export failure is a fallback rather than a stop,
and so the two can be compared against each other as well as against torch.

A `--cond-map` checkpoint (E16: the per-pixel noise plane) exports ONE shape instead, `mapped`: an
`image` input [N,3,256,256] and a `plane` input [N,1,256,256], the plane in `StretchedNoise.PlaneScale`
units exactly as the cache stores it. The host computes the plane (N2nLinearRunner, through the same
`StretchedNoise.Plane` the eval planes came from), so there is no estimator to bake; the tile stays fixed at
256 because the plane's two low-passes see the tile's edges, which makes the tile part of the plane's
definition. Parity is measured on real master tiles WITH their stored planes (`--eval`, a cache prepared
with planes).

Both are checked on REAL master tiles from the eval cache, not on noise: the estimator reads
the darkest half of the luminance, so a uniform-random tile exercises none of the distribution
the model actually meets. The run also writes a fixture of per-tile sigma values, which is what
lets a C# unit test pin the estimator without shipping a torch dependency.

Usage (from this directory; --cache defaults to n2n-d8 under TIANWEN_SCRATCH, see n2n_paths.py):
  python n2n_export.py --ckpt n2n_v19d_s2_final.pt
"""
import argparse
import io
import json
import os
import sys

import numpy as np

import n2n_smoke as S
from n2n_paths import cache

EVAL = cache("n2n-eval4")
OPSET = 17
TILE = 256


def reference_sigma(tile):
    """`bg_sigma_torch` in numpy, so the fixture is independent of the torch call it pins.

    Every pixel in the darkest half sits below the median, so |v - med| there is med - v, and
    the median of that is the 25th percentile measured down from the median. Stated here in
    the plainest possible form because this is the expression C# has to reproduce.
    """
    lum = tile.mean(axis=0).ravel().astype(np.float64)
    med = np.quantile(lum, 0.5)
    q25 = np.quantile(lum, 0.25)
    return (med - q25) * S.SIGMA_SCALE


def sorted_quantile(flat_sorted, q):
    """`torch.quantile`'s linear interpolation, spelled out over an already-sorted row.

    `aten::quantile` has no opset-17 lowering, so the baked graph cannot call it. The
    definition is a position and a lerp: q lands at q*(n-1) in the sorted row, between two
    samples. Verified bit-identical to `bg_sigma_torch` on real tiles before being adopted --
    a "should be equivalent" reimplementation of the estimator is exactly the kind of thing
    that shifts the conditioning by a few percent and is invisible in the output.
    """
    n = flat_sorted.shape[1]
    pos = q * (n - 1)
    lo = int(pos)
    hi = min(lo + 1, n - 1)
    frac = pos - lo
    a = flat_sorted[:, lo:lo + 1]
    b = flat_sorted[:, hi:hi + 1]
    return a + (b - a) * frac


def build_wrappers(model, planes):
    import torch
    import torch.nn as nn

    class Baked(nn.Module):
        """Image in, denoised image out. Sigma is computed inside, so it cannot be mis-fed.

        SPATIALLY FIXED AT 256 on purpose, and this is a correctness constraint rather than a
        packaging shortcut. The sigma estimator reads the darkest half of the tile it is given,
        so its support region IS the tile: measured over 512 px it is a different statistic
        from the one the model was trained against, and a dynamic spatial axis would let a
        caller change the conditioning silently just by chunking differently. Fixing it makes
        the tile size the model's own contract, which is what it always was.
        """

        def __init__(self):
            super().__init__()
            self.net = model

        def forward(self, x, strength):
            b = x.shape[0]
            flat = x[:, :S.CH].mean(dim=1).reshape(b, -1).float()
            fs, _ = torch.sort(flat, dim=1)
            sigma = (sorted_quantile(fs, 0.5) - sorted_quantile(fs, 0.25)).view(b, 1, 1, 1)
            s = sigma * S.SIGMA_SCALE * strength
            xc = torch.cat([x, s.expand(-1, -1, x.shape[2], x.shape[3])], dim=1)
            return self.net(xc)

    class Plain(nn.Module):
        """The bare net: caller supplies the already-concatenated CH+planes input."""

        def __init__(self):
            super().__init__()
            self.net = model

        def forward(self, xc):
            return self.net(xc)

    return Baked().eval(), Plain().eval()


def export_mapped(model, a, nparam):
    """A --cond-map checkpoint: image + stored plane in, denoised image out, checked against torch on real
    master tiles and the planes the cache holds for them."""
    import torch
    import torch.nn as nn

    class Mapped(nn.Module):
        def __init__(self):
            super().__init__()
            self.net = model

        def forward(self, image, plane):
            return self.net(torch.cat([image, plane], dim=1))

    mm, meta = S.open_cache(a.eval)
    sig, has = S.open_sigma(a.eval, meta)
    if sig is None:
        raise SystemExit(f"{a.eval} holds no planes; a --cond-map export is checked on tiles WITH their planes")
    ok = [i for i in range(meta["cells"]) if has[i, S.SLOT_MASTER]]
    idx = np.asarray(ok)[np.linspace(0, len(ok) - 1, a.tiles).astype(int)]
    tiles = np.asarray(mm[idx, S.SLOT_MASTER], dtype=np.float32)
    planes = np.asarray(sig[idx, S.SLOT_MASTER], dtype=np.float32)[:, None]
    print(f"parity tiles: {tiles.shape} and planes {planes.shape} from {a.eval}")
    with torch.no_grad():
        ref = model(S.with_plane(torch.from_numpy(tiles), torch.from_numpy(planes[:, 0]))).numpy()
    scale = float(np.mean([np.std(t.mean(axis=0)) for t in tiles]))
    print(f"tile signal sigma ~{scale:.5f}\n")

    path = os.path.join(os.path.abspath(a.out), a.name or os.path.splitext(a.ckpt)[0] + ".onnx")
    mod = Mapped().eval()
    mb = export(mod, (torch.from_numpy(tiles[:1]), torch.from_numpy(planes[:1])), path, ["image", "plane"],
                {"image": {0: "n"}, "plane": {0: "n"}, "output": {0: "n"}})
    got, names = run_ort(path, {"image": tiles, "plane": planes})
    print(f"mapped -> {os.path.basename(path)} ({mb:.2f} MiB), inputs {names}")
    worst = report("mapped", ref, got, scale)
    meta_out = {
        "checkpoint": a.ckpt, "cache": a.cache, "eval": a.eval, "cond_map": True, "model_file": os.path.basename(path),
        "params": int(nparam), "opset": OPSET, "tile": TILE, "border_px": S.BORDER, "plane_scale": S.PLANE_SCALE,
        "parity_max_abs": worst, "tile_signal_sigma": scale, "parity_cells": [int(i) for i in idx],
    }
    with io.open(os.path.splitext(path)[0] + "_export.json", "w", encoding="utf-8", newline="\n") as f:
        json.dump(meta_out, f, indent=1)
    print(f"\nwritten {os.path.basename(os.path.splitext(path)[0])}_export.json")
    print(f"written {os.path.basename(write_contract(path))}")
    return 0


def write_contract(path):
    """The model contract (#824, ModelContract in C#) beside a `mapped` export: what the graph was trained on, which the
    loader checks against what the runner feeds before it builds a session, and refuses the model without. Written from
    the trainer's own constants, never typed by hand: the domain the cache tiles are stored in (the exporter's whole-frame
    MTF stretch to n2n_operator.TARGET_MEDIAN), the plane's units (StretchedNoise.PlaneScale) and the weights' SHA-256.
    The C# reader is strict, so this writes no field it does not know."""
    import hashlib

    import n2n_operator as OP

    with open(path, "rb") as f:
        sha = hashlib.sha256(f.read()).hexdigest()
    contract = {
        "contractVersion": 1,
        "model": os.path.basename(path),
        "onnxSha256": sha,
        "domain": "mtfStretched",
        "stretchMedianTarget": OP.TARGET_MEDIAN,
        "inputs": [
            {"name": "image", "role": "image", "channels": 3, "height": TILE, "width": TILE},
            {"name": "plane", "role": "plane", "channels": 1, "height": TILE, "width": TILE, "scale": S.PLANE_SCALE},
        ],
        "output": "output",
    }
    out = os.path.splitext(path)[0] + ".contract.json"
    with io.open(out, "w", encoding="utf-8", newline="\n") as f:
        json.dump(contract, f, indent=2)
        f.write("\n")
    return out


def export(mod, args, path, input_names, dynamic_axes):
    import torch
    with io.BytesIO() as _probe:
        pass
    torch.onnx.export(
        mod, args, path,
        input_names=input_names, output_names=["output"],
        dynamic_axes=dynamic_axes, opset_version=OPSET,
        do_constant_folding=True, dynamo=False)
    return os.path.getsize(path) / 2**20


def run_ort(path, feeds):
    import onnxruntime as ort
    so = ort.SessionOptions()
    so.log_severity_level = 3
    sess = ort.InferenceSession(path, so, providers=["CPUExecutionProvider"])
    return sess.run(None, feeds)[0], [i.name for i in sess.get_inputs()]


def report(name, torch_out, ort_out, scale):
    d = np.abs(torch_out - ort_out)
    print(f"  {name:8s} max |diff| {d.max():.3e}   mean {d.mean():.3e}   "
          f"max/signal-sigma {d.max() / scale:.3e}")
    return float(d.max())


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--cache", default=cache("n2n-d8"))
    p.add_argument("--ckpt", default="n2n_v19d_s2_final.pt")
    p.add_argument("--out", default=".")
    p.add_argument("--tiles", type=int, default=8)
    p.add_argument("--strength", type=float, default=1.0)
    p.add_argument("--eval", default=EVAL, help="the cache the parity tiles come from; a --cond-map checkpoint "
                                                "needs one prepared with planes")
    p.add_argument("--name", default=None, help="the ONNX file name (default: the checkpoint's stem)")
    args = p.parse_args()

    import torch

    model, planes = S.load_model(args.cache, args.ckpt, "cpu")
    model.eval()
    nparam = sum(q.numel() for q in model.parameters())
    print(f"{args.ckpt}: cond planes {planes}, cond map {bool(getattr(model, 'cond_map', False))}, {nparam/1e6:.3f} M params")
    if getattr(model, "cond_map", False):
        return export_mapped(model, args, nparam)
    if planes != 1:
        print(f"NOTE: cond planes is {planes}, not the scalar 1 this exporter was written for.")

    mm, meta = S.open_cache(EVAL)
    idx = np.linspace(0, mm.shape[0] - 1, args.tiles).astype(int)
    tiles = np.asarray(mm[idx, S.SLOT_MASTER], dtype=np.float32)
    print(f"parity tiles: {tiles.shape} from {EVAL}")

    x = torch.from_numpy(tiles)
    st = torch.tensor(args.strength, dtype=torch.float32)

    # The reference the exports are judged against, plus the scale to judge them ON: a
    # difference is only meaningful next to the background noise of the tiles it is measured in.
    with torch.no_grad():
        xc = S.with_sigma(x, strength=args.strength, planes=planes)
        ref = model(xc).numpy()
    scale = float(np.mean([np.std(t.mean(axis=0)) for t in tiles]))
    print(f"tile signal sigma ~{scale:.5f}\n")

    baked, plain = build_wrappers(model, planes)
    outdir = os.path.abspath(args.out)
    stem = os.path.splitext(args.ckpt)[0]
    results = {}

    # ---- baked: image + strength scalar, sigma computed in-graph
    baked_path = os.path.join(outdir, stem + ".onnx")
    try:
        mb = export(baked, (x[:1], st), baked_path, ["image", "strength"],
                    {"image": {0: "n"}, "output": {0: "n"}})
        got, names = run_ort(baked_path, {"image": tiles,
                                          "strength": np.array(args.strength, dtype=np.float32)})
        print(f"baked  -> {os.path.basename(baked_path)} ({mb:.2f} MiB), inputs {names}")
        results["baked"] = report("baked", ref, got, scale)
    except Exception as e:
        print(f"baked  -> FAILED: {type(e).__name__}: {str(e)[:300]}")
        results["baked"] = None

    # ---- plain: caller-supplied 4-channel input
    plain_path = os.path.join(outdir, stem + "_plain.onnx")
    try:
        mb = export(plain, (xc[:1],), plain_path, ["input"],
                    {"input": {0: "n", 2: "h", 3: "w"}, "output": {0: "n", 2: "h", 3: "w"}})
        got, names = run_ort(plain_path, {"input": xc.numpy()})
        print(f"plain  -> {os.path.basename(plain_path)} ({mb:.2f} MiB), inputs {names}")
        results["plain"] = report("plain", ref, got, scale)
    except Exception as e:
        print(f"plain  -> FAILED: {type(e).__name__}: {str(e)[:300]}")
        results["plain"] = None

    # ---- the estimator fixture: what C# has to reproduce, and what torch actually produced
    print("\nsigma fixture (numpy reference vs torch, per tile):")
    with torch.no_grad():
        sig_torch = (S.bg_sigma_torch(x) * S.SIGMA_SCALE).view(-1).numpy()
    fixture = []
    worst = 0.0
    for k, t in enumerate(tiles):
        r = reference_sigma(t)
        worst = max(worst, abs(r - sig_torch[k]))
        fixture.append({"cell": int(idx[k]), "sigma": float(sig_torch[k]),
                        "numpy_ref": float(r)})
        print(f"  cell {idx[k]:5d}  torch {sig_torch[k]:.6f}   numpy {r:.6f}")
    print(f"  max |numpy - torch| {worst:.3e}")

    meta_out = {
        "checkpoint": args.ckpt, "cache": args.cache, "cond_planes": planes,
        "params": int(nparam), "opset": OPSET, "tile": TILE,
        "border_px": S.BORDER, "sigma_scale": S.SIGMA_SCALE,
        "strength": args.strength, "parity_max_abs": results,
        "tile_signal_sigma": scale, "sigma_fixture": fixture,
    }
    with io.open(os.path.join(outdir, stem + "_export.json"), "w", encoding="utf-8") as f:
        json.dump(meta_out, f, indent=1)
    print(f"\nwritten {stem}_export.json")
    return 0


if __name__ == "__main__":
    sys.exit(main())
