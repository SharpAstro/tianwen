r"""E7 (#844): E3.4d's operator and its prior as ONE ONNX graph, the kernel a runtime input, proved against torch.

E7.4's verdict names the port's shape: "the operator as an ONNX graph taking the kernel as an input tensor (one export,
the kernel a runtime value, not baked), the round trip's resample either side of it". This writes that graph from a
`--operator rl` checkpoint (E3.4d's `e34d_s0_final.pt` by default) and a tiny graph with the same signature for the C#
tests, and measures the export against the torch module on a real master, called the way the readouts call it.

What the graph takes and gives (docs/plans/deconvolver-training.md, E3.0 to E3.4d and E7.1 to E7.5):

- **image [N, 3, H, W]**: the tile STRETCHED with the frame's own parameters, in [0, 1]: per channel the minimum
  subtracted and clamped at zero, then the midtones transfer function with the balance that lands the channel's median
  on 0.25 (`n2n_operator.session_stretch_params` on the whole native frame, unit-scaled by its observed peak). The prior
  was trained and read in that domain. H and W are free but must be multiples of 4 (the prior's two poolings); the
  readouts used multiples of 16.
- **kernel [3, k, k]**: one normalised kernel per CHANNEL, k odd, the three zero-padded about their centre to the
  largest one's size. Every readout deconvolved each channel in its own pass of the whole operator, with that channel's
  kernel on all three planes, and kept only that channel's plane (the prior mixes the planes, so this is not three
  independent deconvolutions); the graph runs the three passes itself and returns the diagonal, so a caller cannot get
  the protocol wrong. A shared kernel is three copies (and costs the same three passes). The kernel is shared by every
  tile of a batch: a window with a kernel of its own is a call of its own.
- **stretch_min [3], stretch_balance [3]**: the stretch's per-channel minimum and midtones balance. Richardson-Lucy is
  physics in LINEAR units, so the graph unstretches (`MTF(1 - balance, y) + min`), iterates, and restretches with the
  same numbers; the prior between iterations restretches, corrects and unstretches with them too.
- **output [N, 3, H, W]**: the deconvolved tile, stretched with the same parameters.

K (20 for E3.4d) and the prior's place (after every update) are the checkpoint's and are baked: the prior was trained at
that K, so it is not a dial. The 1.28x resample round trip is NOT in the graph: the readouts resampled the whole frame
with `scipy.ndimage.zoom(order=3)` (a cubic B-spline with a global prefilter, which ONNX's Resize, a local cubic
convolution, does not reproduce), up in LINEAR before the stretch and down in STRETCHED after the tiles are stitched,
with the kernel's width scaled by the same factor; that is the caller's, and so is the factor (E7 chooses it with the
kernel). The exporter is torch.export's, not the TorchScript one `n2n_export.py` uses: the TorchScript Conv needs the
weight's shape at export time, and a kernel whose size is a runtime value is the point.

Usage (everything runs on the CPU; to keep torch off the GPU entirely, note that on Windows an EMPTY
CUDA_VISIBLE_DEVICES still shows torch the card, and -1 hides it):
  CUDA_VISIBLE_DEVICES=-1 python n2n_operator_export.py --out C:/temp/e2/e7-export
"""
import argparse
import io
import json
import os
import sys
import time

import numpy as np

import n2n_operator as OP
from n2n_paths import bake, cache

OPSET = 18
CHANNELS = 3
INPUTS = ("image", "kernel", "stretch_min", "stretch_balance")
STATUE_MASTER = os.path.join(bake("2026-09-29-full"), "session-masters",
                             "SVBONY-SV605CC_Optolong-L-Quad-Enhance_Statue-of-Liberty-Nebula_2026-02-14_SVBONY SV605CC_"
                             "Statue of Liberty Nebula_Optolong L-Quad Enhance.fits")


# --------------------------------------------------------------------------- the graph
def build_graph(operator):
    """The module the export traces: `RLOperator` with its prior, the label planes replaced by named inputs and the
    readouts' per-channel passes inside. Every expression is the operator's own (`n2n_operator.unstretch`, `restretch`,
    `StretchedPrior.forward`, `rl_deconvolve`'s step) in its order; only the layout differs, the three passes laid side
    by side as nine grouped channels where `rl_deconvolve` lays a batch's samples."""
    import torch
    import torch.nn.functional as F

    if operator.prior is None:
        raise SystemExit("the checkpoint carries no prior; E3.0, the bare operator, needs no export")

    class OperatorGraph(torch.nn.Module):
        def __init__(self):
            super().__init__()
            self.iterations = int(operator.iterations)
            self.prior = operator.prior

        def forward(self, image, kernel, stretch_min, stretch_balance):
            c = CHANNELS
            h, w = image.shape[2], image.shape[3]
            k = kernel.shape[-1]
            r = k // 2
            mins = stretch_min.view(1, c)
            betas = stretch_balance.view(1, c)
            linear = OP.unstretch(image, mins, betas)
            data = torch.where(torch.isfinite(linear) & (linear >= 0), linear, torch.zeros_like(linear))
            # Pass p deconvolves all three planes with kernel p: [N, pass, channel, H, W], pass-major, so a reshape to
            # [N * 3, 3, H, W] is the prior's batch and a reshape to [N, 9, H, W] the convolution's grouped channels,
            # channel p * 3 + ch convolved with kernel p.
            data = data.unsqueeze(1).expand(-1, c, c, h, w).reshape(-1, c, h, w)
            weight = kernel.unsqueeze(1).expand(c, c, k, k).reshape(c * c, 1, k, k)
            adjoint = torch.flip(weight, dims=(-1, -2))

            def convolve(t, wt):
                padded = F.pad(t.reshape(-1, c * c, h, w), (r, r, r, r), mode="replicate")
                return F.conv2d(padded, wt, groups=c * c).reshape(-1, c, h, w)

            estimate = data
            for it in range(self.iterations):
                blurred = convolve(estimate, weight)
                ratio = torch.where(blurred > OP.DENOMINATOR_FLOOR,
                                    data / blurred.clamp_min(OP.DENOMINATOR_FLOOR),
                                    torch.ones_like(blurred))
                correction = convolve(ratio, adjoint)
                nxt = estimate * correction
                nxt = torch.where(torch.isfinite(nxt) & (nxt > 0), nxt, torch.zeros_like(nxt))
                estimate = self.prior(nxt, it, mins, betas)
            out = OP.restretch(estimate, mins, betas).reshape(-1, c, c, h, w)
            return torch.stack([out[:, p, p] for p in range(c)], dim=1)

    return OperatorGraph().eval()


def channel_kernels(fwhms, beta):
    """[3, k, k] float32: `n2n_operator.moffat_kernel` per channel at the three's common support, each zero-padded about
    its centre. The width goes through float32 first because the readouts carried it in a float32 label plane."""
    fwhms = [float(np.float32(f)) for f in fwhms]
    radius = max(OP.moffat_radius(f, beta) if f > 0 else 1 for f in fwhms)
    return np.stack([OP.moffat_kernel(f, beta, radius) for f in fwhms]).astype(np.float32)


def torch_reference(operator, stretched, fwhms, beta, mins, betas):
    """The readouts' call, verbatim (`n2n_operator_real.main`, `n2n_operator_master.run_tiled`): the operator once per
    channel with that channel's kernel in the label planes, that channel's plane kept. stretched: [N, 3, H, W]."""
    import torch
    import n2n_deconv_gate as DG

    x = torch.from_numpy(np.ascontiguousarray(stretched, dtype=np.float32))
    labels = np.zeros((x.shape[0], OP.LABEL_COUNT), dtype=np.float32)
    labels[:, OP.LABEL_MIN] = mins
    labels[:, OP.LABEL_BETA] = betas
    labels[:, OP.LABEL_PSF01] = 0.5
    out = np.empty(x.shape, dtype=np.float32)
    with torch.no_grad():
        for c, fwhm in enumerate(fwhms):
            lab = labels.copy()
            lab[:, OP.LABEL_KERNEL_FWHM] = fwhm
            lab[:, OP.LABEL_KERNEL_BETA] = beta
            out[:, c] = operator(DG.with_psf01(x, lab))[:, c].numpy()
    return out


def export(graph, path, sample, metadata):
    """torch.export's exporter with the dynamic dimensions declared with their constraints (H and W multiples of 4, k
    odd); then the exporter's per-node stack traces dropped (80 percent of the file), the symbolic dimensions renamed to
    the contract's names, and the contract's fields stored in the model's metadata."""
    import onnx
    import torch
    from torch.export import Dim
    n = Dim("n", min=1, max=1024)
    h4, w4 = Dim("h4", min=2, max=8192), Dim("w4", min=2, max=8192)
    kr = Dim("kr", min=1, max=127)
    shapes = {"image": {0: n, 2: 4 * h4, 3: 4 * w4}, "kernel": {1: 2 * kr + 1, 2: 2 * kr + 1},
              "stretch_min": None, "stretch_balance": None}
    # One self-contained file: the exporter's default puts the weights in a `.data` file beside the graph, a second file
    # the runtime has to find and the LFS pattern that ships models has to carry.
    torch.onnx.export(graph, tuple(torch.from_numpy(np.asarray(a, dtype=np.float32)) for a in sample), path,
                      input_names=list(INPUTS), output_names=["output"], dynamic_shapes=shapes,
                      opset_version=OPSET, dynamo=True, external_data=False, verbose=False)
    model = onnx.load(path)
    for node in model.graph.node:
        del node.metadata_props[:]
    names = {"4*h4": "h", "4*w4": "w", "2*kr + 1": "k"}
    for v in list(model.graph.input) + list(model.graph.output) + list(model.graph.value_info):
        for d in v.type.tensor_type.shape.dim:
            if d.dim_param in names:
                d.dim_param = names[d.dim_param]
    del model.metadata_props[:]
    for key, value in metadata.items():
        model.metadata_props.add(key=key, value=value if isinstance(value, str) else json.dumps(value))
    onnx.checker.check_model(model)
    onnx.save(model, path)
    return os.path.getsize(path) / 2 ** 20


def session(path, threads=0):
    import onnxruntime as ort
    so = ort.SessionOptions()
    so.log_severity_level = 3
    so.intra_op_num_threads = threads
    return ort.InferenceSession(path, so, providers=["CPUExecutionProvider"])


def run_graph(sess, stretched, kernel, mins, betas):
    """The graph on [N, 3, H, W] stretched tiles with one [3, k, k] kernel and the frame's stretch."""
    return sess.run(None, {"image": np.ascontiguousarray(stretched, dtype=np.float32),
                           "kernel": np.ascontiguousarray(kernel, dtype=np.float32),
                           "stretch_min": np.asarray(mins, dtype=np.float32),
                           "stretch_balance": np.asarray(betas, dtype=np.float32)})[0]


def contract(iterations, prior_base, prior_every, checkpoint):
    """What the graph's metadata and its contract JSON both state: the inputs, their domains, and what is baked."""
    return {
        "tianwen.model": "deconvolver: unrolled Richardson-Lucy with a residual U-Net prior after every update (E3.4d)",
        "tianwen.checkpoint": checkpoint, "tianwen.iterations": str(iterations), "tianwen.prior_base": str(prior_base),
        "tianwen.prior_every": str(prior_every),
        "tianwen.image": "float32 [n, 3, h, w], h and w multiples of 4: the tile STRETCHED, per channel "
                         "MTF(stretch_balance, max(unit - stretch_min, 0)), in [0, 1]",
        "tianwen.kernel": "float32 [3, k, k], k odd: per channel a normalised kernel (sum 1), the three zero-padded about "
                          "their centre to one size; each channel is deconvolved in its own pass with its own kernel on "
                          "all three planes, its plane kept",
        "tianwen.stretch_min": "float32 [3]: the unit frame's per-channel minimum",
        "tianwen.stretch_balance": "float32 [3]: the per-channel midtones balance landing the shifted median on 0.25",
        "tianwen.output": "float32 [n, 3, h, w]: the deconvolved tile, stretched with the same parameters",
        "tianwen.round_trip": "not in the graph: the caller resamples the frame up (linear) before and down (stretched) "
                              "after, and scales the kernel's width by the same factor",
    }


# --------------------------------------------------------------------------- the frame, as the readouts take it
def read_frame(path):
    """A linear master's planes [3, H, W] unit-scaled, NaN as zero, the unit divisor and the frame's own stretch, as
    `n2n_operator_master` derives them. `n2n_operator_real` (the E3.4d read) skips NaN instead, so on a master whose
    canvas ring is NaN its minimum is the darkest covered pixel where this one's is 0: the same graph, another domain."""
    from astropy.io import fits
    with fits.open(path, memmap=False) as h:
        hdu = next(x for x in h if x.data is not None)
        data = np.asarray(hdu.data, dtype=np.float32)
    if data.ndim != 3 or data.shape[0] != CHANNELS:
        raise SystemExit(f"{path}: expected a 3-plane master, got {data.shape}")
    data = np.nan_to_num(data, nan=0.0)
    data_max = float(data.max())
    divisor = data_max if data_max > 1.0 else 1.0
    mins, betas = OP.session_stretch_params(data, divisor)
    inv = np.float32(1.0 / divisor) if divisor != 1.0 else np.float32(1.0)
    return data * inv, divisor, mins, betas


def stretch(unit, mins, betas):
    """`n2n_operator_real.stretch`: per channel the minimum off, clamped at zero, the MTF in float64, back to float32."""
    out = np.empty_like(unit, dtype=np.float32)
    for c in range(unit.shape[0]):
        out[c] = OP.mtf(betas[c], np.clip(unit[c] - mins[c], 0.0, None).astype(np.float64)).astype(np.float32)
    return out


def unstretch(stretched, mins, betas):
    """`n2n_operator_master.unstretch_np`: the stretched planes back to unit linear."""
    out = np.empty_like(stretched, dtype=np.float32)
    for c in range(stretched.shape[-3]):
        y = np.clip(stretched[..., c, :, :], 0.0, 1.0).astype(np.float64)
        out[..., c, :, :] = (OP.mtf(1.0 - betas[c], y) + mins[c]).astype(np.float32)
    return out


def zoom_planes(planes, factor):
    from scipy.ndimage import zoom as ndzoom
    return np.stack([ndzoom(p, factor, order=3) for p in planes]).astype(np.float32)


def diff(a, b):
    d = np.abs(np.asarray(a, dtype=np.float64) - np.asarray(b, dtype=np.float64))
    return float(d.max()), float(d.mean())


# --------------------------------------------------------------------------- the checks
def parity(operator, graph, sess, args, log):
    """Torch's readout call against the graph, in ONNX Runtime and as the torch module the export traced, on a real
    master: the E3.4d read's crop at its zoom and kernels, then native tiles at kernel sets of other sizes, a batch of
    two and a non-square tile. Returns the rows and the frame's stretch."""
    import torch
    unit, divisor, mins, betas = read_frame(args.master)
    zero = float((unit == 0).all(axis=0).mean())
    log(f"parity frame {args.master}\n  {unit.shape[2]}x{unit.shape[1]}, divisor {divisor:.6g}, min ({mins[0]:.6g}, "
        f"{mins[1]:.6g}, {mins[2]:.6g}), balance ({betas[0]:.6f}, {betas[1]:.6f}, {betas[2]:.6f}), {zero:.4f} of the "
        f"pixels exact zero on every plane (the canvas ring)")
    fwhms = [float(v) for v in args.kernels.split(",")]
    rows = []

    def check(name, stretched, kfwhms, beta, back=None):
        kernel = channel_kernels(kfwhms, beta)
        t0 = time.perf_counter()
        ref = torch_reference(operator, stretched, kfwhms, beta, mins, betas)
        t1 = time.perf_counter()
        got = run_graph(sess, stretched, kernel, mins, betas)
        t2 = time.perf_counter()
        with torch.no_grad():
            mod = graph(*(torch.from_numpy(np.ascontiguousarray(a, dtype=np.float32))
                          for a in (stretched, kernel, mins, betas))).numpy()
        mx, mean = diff(ref, got)
        lmx, lmean = diff(unstretch(ref, mins, betas), unstretch(got, mins, betas))
        tmx, _ = diff(ref, mod)
        row = {"check": name, "shape": list(stretched.shape), "kernel_fwhm_px": [round(f, 6) for f in kfwhms],
               "beta": beta, "kernel_size": int(kernel.shape[-1]), "max_abs": mx, "mean_abs": mean,
               "max_abs_linear_unit": lmx, "mean_abs_linear_unit": lmean, "module_max_abs": tmx,
               "torch_s": round(t1 - t0, 1), "onnx_s": round(t2 - t1, 1),
               "output_moved_max": float(np.abs(ref - stretched).max()),
               "output_moved_mean": float(np.abs(ref - stretched).mean())}
        msg = (f"  {name:40s} {str(tuple(stretched.shape)):19s} k={kernel.shape[-1]:2d}  onnx max |diff| {mx:.3e} mean "
               f"{mean:.3e} (unit linear max {lmx:.3e}); module {tmx:.3e}; the operator moved the tile up to "
               f"{row['output_moved_max']:.3f}, mean {row['output_moved_mean']:.4f}; torch {t1 - t0:.0f} s, onnx {t2 - t1:.0f} s")
        if back is not None:
            rb, gb = back(ref), back(got)
            bmx, bmean = diff(rb, gb)
            row.update(roundtrip_max_abs=bmx, roundtrip_mean_abs=bmean)
            msg += f"\n  {'':40s} round trip back to native: max |diff| {bmx:.3e}  mean {bmean:.3e}"
        log(msg)
        rows.append(row)
        return ref, got

    # 1. The E3.4d read's crop (`n2n_operator_real` --crop 0,1024,1024 --zoom 1.28 --roundtrip): the linear crop zoomed
    #    to a multiple of 16, stretched with the native frame's parameters, the kernels scaled by the effective factor.
    cx, cy, size = (int(v) for v in args.crop.split(","))
    crop = unit[:, cy:cy + size, cx:cx + size]
    side = int(round(size * args.zoom / 16)) * 16
    zoom_eff = side / size
    zoomed = zoom_planes(crop, zoom_eff)
    if zoomed.shape[1:] != (side, side):
        raise SystemExit(f"zoom landed on {zoomed.shape}, wanted {side}")
    ref, got = check(f"readout crop {args.crop} at {zoom_eff:.5f}", stretch(zoomed, mins, betas)[None],
                     [f * zoom_eff for f in fwhms], args.beta, back=lambda out: zoom_planes(out[0], size / side))
    np.save(os.path.join(args.out, "parity_readout_torch.npy"), ref[0])
    np.save(os.path.join(args.out, "parity_readout_onnx.npy"), got[0])

    # 2. Native tiles at other kernel sets: the readout's widths, a mixed set whose radii differ (the graph's common
    #    zero-padded support against torch's per-channel kernel of its own size), a light-winged wide set and a Gaussian;
    #    then a batch of two and a non-square tile.
    h, w = unit.shape[1:]
    places = [(h // 4, w // 4), (h // 2, w // 2), (3 * h // 4, 3 * w // 4)]
    tile = 256
    tiles = [stretch(unit[:, y:y + tile, x:x + tile], mins, betas) for y, x in places]
    sets = [("native, E3.4d kernels", fwhms, args.beta),
            ("native, mixed radii", [0.5, 1.5, 3.0], 2.5),
            ("native, wide light wings", [2.0, 2.2, 2.4], 1.5),
            ("native, Gaussian", [1.2, 1.3, 1.4], float("inf"))]
    for i, (name, kf, beta) in enumerate(sets):
        check(f"{name} at {places[i % 3]}", tiles[i % 3][None], kf, beta)
    check("batch of two", np.stack(tiles[:2]), fwhms, args.beta)
    y, x = places[1]
    check("non-square 192x320", stretch(unit[:, y:y + 192, x:x + 320], mins, betas)[None], fwhms, args.beta)

    # 3. A small real sample for the C# parity test against the shipped graph: the readout crop's corner at native.
    sample = stretch(unit[:, cy + 256:cy + 320, cx + 256:cx + 320], mins, betas)[None]
    kernel = channel_kernels(fwhms, args.beta)
    expected = torch_reference(operator, sample, fwhms, args.beta, mins, betas)
    got = run_graph(sess, sample, kernel, mins, betas)
    write_io(os.path.join(args.out, os.path.splitext(args.name)[0] + "_io.json"), sample, kernel, fwhms, args.beta,
             mins, betas, expected, diff(expected, got)[0], {"master": args.master, "x": cx + 256, "y": cy + 256})
    return rows, (mins, betas, divisor)


def write_io(path, image, kernel, fwhms, beta, mins, betas, expected, onnx_max_abs, extra):
    """One input and torch's output, flat row-major float32, for a C# test that feeds the graph and compares."""
    with io.open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(dict(extra, **{
            "note": "row-major float32; image and expected [n, 3, h, w], kernel [3, k, k] = channel_kernels(kernel_fwhm_px, "
                    "kernel_beta); expected is torch's readout call (the operator once per channel, that plane kept)",
            "image_shape": list(image.shape), "kernel_shape": list(kernel.shape),
            "kernel_fwhm_px": [float(v) for v in fwhms], "kernel_beta": beta,
            "stretch_min": [float(v) for v in np.float32(mins)],
            "stretch_balance": [float(v) for v in np.float32(betas)],
            "onnx_max_abs": onnx_max_abs,
            "image": [float(v) for v in np.asarray(image, np.float32).ravel()],
            "kernel": [float(v) for v in np.asarray(kernel, np.float32).ravel()],
            "expected": [float(v) for v in np.asarray(expected, np.float32).ravel()]}), f)


# --------------------------------------------------------------------------- the fixture
def fixture_operator(seed, base, iterations):
    """A tiny operator with E3.4d's structure: the same U-Net (`n2n_smoke.build_model`, upsampling, no conditioning) at a
    small width, seeded, its output convolution re-drawn after `StretchedPrior` zeroes it, so the prior is NOT the
    identity (a C# test must be able to see a prior that went missing)."""
    import torch
    import n2n_smoke as S
    S.use_channels(CHANNELS)
    torch.manual_seed(seed)
    prior = OP.StretchedPrior(S.build_model(base, upsample=True, cond=0), every=1)
    with torch.no_grad():
        torch.nn.init.normal_(prior.net.out.weight, std=0.05)
        torch.nn.init.normal_(prior.net.out.bias, std=0.005)
    return OP.RLOperator(iterations, prior=prior, recompute=False).eval()


def fixture_input(seed, size):
    """A small stretched RGB tile with a sky, its noise and a few Moffat stars, and the stretch it was made with."""
    rng = np.random.default_rng(seed)
    yy, xx = np.mgrid[0:size, 0:size].astype(np.float64)
    lin = np.full((CHANNELS, size, size), 0.01) + rng.normal(0.0, 0.001, (CHANNELS, size, size))
    for _ in range(6):
        cy, cx = rng.uniform(3, size - 3, 2)
        amp = rng.uniform(0.05, 0.6)
        alpha = rng.uniform(1.0, 1.8)
        star = amp * (1.0 + ((yy - cy) ** 2 + (xx - cx) ** 2) / alpha ** 2) ** -3.0
        lin += star[None] * np.array([1.0, 0.9, 0.8])[:, None, None]
    lin = np.clip(lin, 0.0, 1.0).astype(np.float32)
    mins, betas = OP.session_stretch_params(lin, 1.0)
    return stretch(lin, mins, betas)[None], mins, betas


def write_fixture(args, log):
    op = fixture_operator(args.fixture_seed, args.fixture_base, args.fixture_k)
    nparam = sum(p.numel() for p in op.parameters())
    path = os.path.join(args.out, args.fixture_name)
    image, mins, betas = fixture_input(args.fixture_seed, args.fixture_size)
    fwhms, beta = [0.9, 1.1, 1.6], 4.0
    kernel = channel_kernels(fwhms, beta)
    meta = contract(args.fixture_k, args.fixture_base, 1, f"none: a random prior, seed {args.fixture_seed}")
    mb = export(build_graph(op), path, (image, kernel, mins, betas), meta)
    ref = torch_reference(op, image, fwhms, beta, mins, betas)
    got = run_graph(session(path, args.threads), image, kernel, mins, betas)
    mx, mean = diff(ref, got)
    bare = torch_reference(OP.RLOperator(op.iterations, prior=None, recompute=False).eval(), image, fwhms, beta, mins, betas)
    moved = float(np.abs(ref - bare).max())
    log(f"fixture -> {path} ({mb * 1024:.1f} KiB, {nparam} params, K={args.fixture_k}, base {args.fixture_base}): onnx "
        f"against the readout call max |diff| {mx:.3e} mean {mean:.3e}; the prior moves the output up to {moved:.3e} "
        f"from the bare operator's")
    io_path = os.path.splitext(path)[0] + "_io.json"
    write_io(io_path, image, kernel, fwhms, beta, mins, betas, ref, mx,
             {"seed": args.fixture_seed, "iterations": args.fixture_k, "prior_base": args.fixture_base})
    log(f"fixture io -> {io_path}")
    return {"file": os.path.basename(path), "io": os.path.basename(io_path), "params": int(nparam),
            "iterations": args.fixture_k, "prior_base": args.fixture_base, "seed": args.fixture_seed,
            "max_abs": mx, "mean_abs": mean, "prior_moves_max": moved}


# --------------------------------------------------------------------------- main
def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--cache", default=cache("n2n-p2-blur-clamped-sh61"), help="where the checkpoint is")
    p.add_argument("--ckpt", default="e34d_s0_final.pt", help="E3.4d seed 0's final checkpoint, the one that carries to E7")
    p.add_argument("--out", required=True, help="where the graphs, the contract and the parity record go (never the repo)")
    p.add_argument("--name", default="tianwen_deconv_operator_e34d_s0.onnx")
    p.add_argument("--master", default=STATUE_MASTER,
                   help="a LINEAR 3-plane master for the parity check (default: the Statue of Liberty session the E2.10b "
                        "pair was split from, the whole night, as the 2026-09-29 bake stacked it)")
    p.add_argument("--crop", default="0,1024,1024", help="x,y,size: the E3.4d read's primary crop")
    p.add_argument("--kernels", default="0.77,0.91,0.98", help="per-channel kernel FWHM px at native scale (the pair's est-c)")
    p.add_argument("--beta", type=float, default=4.0)
    p.add_argument("--zoom", type=float, default=1.28, help="the round trip's factor (rounded to a multiple of 16 px)")
    p.add_argument("--threads", type=int, default=8, help="CPU threads for torch and ONNX Runtime (the box is shared)")
    p.add_argument("--skip-parity", action="store_true")
    p.add_argument("--fixture-name", default="tianwen_deconv_operator_fixture.onnx")
    p.add_argument("--fixture-seed", type=int, default=844)
    p.add_argument("--fixture-base", type=int, default=2)
    p.add_argument("--fixture-k", type=int, default=2)
    p.add_argument("--fixture-size", type=int, default=32)
    args = p.parse_args()

    import torch
    import n2n_smoke as S
    torch.set_grad_enabled(False)
    torch.set_num_threads(args.threads)
    os.makedirs(args.out, exist_ok=True)
    lines = []

    def log(msg):
        print(msg, flush=True)
        lines.append(msg)

    operator, _ = S.load_model(args.cache, args.ckpt, "cpu")
    if not isinstance(operator, OP.RLOperator):
        raise SystemExit(f"{args.ckpt} is not an operator checkpoint (--operator rl)")
    ck = torch.load(os.path.join(args.cache, args.ckpt), map_location="cpu", weights_only=False)
    nparam = sum(q.numel() for q in operator.parameters())
    log(f"{args.ckpt}: Richardson-Lucy K={operator.iterations}, prior base {ck['prior_base']} every "
        f"{operator.prior.every}, upsample {ck.get('upsample')}, {nparam} params")

    path = os.path.join(args.out, args.name)
    graph = build_graph(operator)
    meta = contract(operator.iterations, ck["prior_base"], operator.prior.every, args.ckpt)
    sample_mins, sample_betas = np.zeros(3, np.float32), np.full(3, 0.1, np.float32)
    sample = stretch(np.full((CHANNELS, 64, 64), 0.02, dtype=np.float32), sample_mins, sample_betas)[None]
    t0 = time.perf_counter()
    mb = export(graph, path, (sample, channel_kernels([1.0, 1.2, 1.4], 4.0), sample_mins, sample_betas), meta)
    sess = session(path, args.threads)
    log(f"graph -> {path} ({mb:.2f} MiB, opset {OPSET}, {time.perf_counter() - t0:.0f} s), inputs "
        + ", ".join(f"{i.name} {i.shape} {i.type}" for i in sess.get_inputs())
        + "; outputs " + ", ".join(f"{o.name} {o.shape} {o.type}" for o in sess.get_outputs()))

    rows, frame = [], None
    if not args.skip_parity:
        rows, frame = parity(operator, graph, sess, args, log)
    fixture = write_fixture(args, log)

    record = {
        "model_file": args.name, "checkpoint": args.ckpt, "cache": args.cache, "opset": OPSET, "params": int(nparam),
        "metadata": meta,
        "kernel_rule": {"shape": "circular Moffat (PsfKernel.Moffat, n2n_operator.moffat_kernel)", "readout_beta": args.beta,
                        "readout_fwhm_px_native": [float(v) for v in args.kernels.split(",")],
                        "scaled_by_the_zoom": True},
        "round_trip": {"in_graph": False, "factor": args.zoom,
                       "up": "scipy.ndimage.zoom(order=3) on the LINEAR unit frame, before the stretch",
                       "down": "scipy.ndimage.zoom(order=3), the exact inverse factor, on the STRETCHED output"},
        "parity": rows, "fixture": fixture,
    }
    if frame is not None:
        record["parity_frame"] = {"master": args.master, "divisor": frame[2],
                                  "stretch_min": [float(v) for v in frame[0]],
                                  "stretch_balance": [float(v) for v in frame[1]]}
    with io.open(os.path.splitext(path)[0] + "_contract.json", "w", encoding="utf-8", newline="\n") as f:
        json.dump(record, f, indent=1)
    with io.open(os.path.join(args.out, "export-log.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")
    log(f"written {os.path.splitext(args.name)[0]}_contract.json and export-log.txt")
    return 0


if __name__ == "__main__":
    sys.exit(main())
