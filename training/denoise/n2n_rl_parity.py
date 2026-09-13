"""Pin the torch Richardson-Lucy operator against the C# implementation it ports.

`RichardsonLucyPortFixtureProbe` (TianWen.Lib.Tests, opt-in through TIANWEN_RL_FIXTURE_DIR) writes a
synthetic star field, its blur by a known Moffat, the C# kernel's weights and the C# estimate after
a fixed number of iterations. This script rebuilds the kernel from the same parameters, runs
`n2n_operator.rl_deconvolve` on the same observed plane, and subtracts.

Why this exists and what it is allowed to prove: a port that is quietly off (a flipped kernel, a
zero-padded border where the C# clamps indices, a different denominator floor) still deconvolves
something, and no gate that does not know the answer can tell. The oracle ceiling every arm is read
against was measured with the C# iteration, so the torch iteration must be the same operator to a
rounding tolerance before its gate reading means what the C# one did. It proves nothing about the
kernel ESTIMATE (that is the exporter's) or about the stretch bridge (that is `--prepare`'s parity
against the cache's own tiles); each has its own check.

Tolerances: the kernel to 1e-6 absolute (the C# stores float32 weights normalised by a float32
reciprocal); the estimate to 2e-4 absolute and 1e-3 relative on samples above 1e-3, which is what
twenty iterations of float32 accumulation in a different order can move. Run:

    set TIANWEN_RL_FIXTURE_DIR=C:/temp/e2/rl-fixture
    dotnet test src/TianWen.Lib.Tests --filter FullyQualifiedName~RichardsonLucyPortFixtureProbe
    python training/denoise/n2n_rl_parity.py C:/temp/e2/rl-fixture
"""
import json
import os
import sys

import numpy as np
import torch

import n2n_operator as OP

KERNEL_TOL = 1e-6
ESTIMATE_ABS_TOL = 2e-4
ESTIMATE_REL_TOL = 1e-3


def read_plane(path, count):
    values = np.fromfile(path, dtype="<f4")
    if values.size != count:
        raise SystemExit(f"{path}: {values.size} samples, expected {count}")
    return values


def main(argv):
    if len(argv) != 2:
        raise SystemExit(__doc__)
    fixture = argv[1]
    with open(os.path.join(fixture, "fixture.json"), encoding="utf-8") as fh:
        meta = json.load(fh)
    size, k_size, iterations = meta["size"], meta["kernelSize"], meta["iterations"]
    truth = read_plane(os.path.join(fixture, "truth.f32"), size * size).reshape(size, size)
    observed = read_plane(os.path.join(fixture, "observed.f32"), size * size).reshape(size, size)
    cs_estimate = read_plane(os.path.join(fixture, "estimate.f32"), size * size).reshape(size, size)
    cs_kernel = read_plane(os.path.join(fixture, "kernel.f32"), k_size * k_size).reshape(k_size, k_size)

    ok = True
    # 1. The kernel: same support, same weights.
    radius = OP.moffat_radius(meta["blurFwhm"], meta["blurBeta"])
    if radius != meta["kernelRadius"]:
        print(f"FAIL kernel radius: torch {radius}, C# {meta['kernelRadius']}")
        ok = False
    py_kernel = OP.moffat_kernel(meta["blurFwhm"], meta["blurBeta"]).astype(np.float32)
    if py_kernel.shape == cs_kernel.shape:
        kd = float(np.abs(py_kernel - cs_kernel).max())
        print(f"kernel {k_size}x{k_size}: max |torch - C#| {kd:.2e} (tol {KERNEL_TOL:.0e}) "
              f"{'ok' if kd <= KERNEL_TOL else 'FAIL'}")
        ok &= kd <= KERNEL_TOL
    else:
        print(f"FAIL kernel shape: torch {py_kernel.shape}, C# {cs_kernel.shape}")
        ok = False

    # 2. The iteration, on CPU and (if present) on the GPU the arms will run on, with the C# kernel
    #    (so this compares the ITERATION alone) and with the rebuilt one (the whole port).
    devices = ["cpu"] + (["cuda"] if torch.cuda.is_available() else [])
    for dev in devices:
        for label, kernel in (("C# kernel", cs_kernel), ("torch kernel", py_kernel)):
            obs = torch.from_numpy(observed)[None, None].to(dev)
            ker = torch.from_numpy(kernel)[None].to(dev)
            with torch.no_grad():
                est = OP.rl_deconvolve(obs, ker, iterations)[0, 0].cpu().numpy()
            diff = np.abs(est - cs_estimate)
            mask = cs_estimate > 1e-3
            rel = float((diff[mask] / cs_estimate[mask]).max()) if mask.any() else 0.0
            worst = float(diff.max())
            passed = worst <= ESTIMATE_ABS_TOL and rel <= ESTIMATE_REL_TOL
            ok &= passed
            print(f"RL K={iterations} on {dev:4s} with {label:12s}: max |diff| {worst:.2e} (tol {ESTIMATE_ABS_TOL:.0e}), "
                  f"max rel {rel:.2e} (tol {ESTIMATE_REL_TOL:.0e}) {'ok' if passed else 'FAIL'}")

    # 3. A sanity line, not a criterion: the C# estimate should be nearer the truth than the observed
    #    was, or the fixture is not exercising a deconvolution at all.
    def rms(a):
        return float(np.sqrt(np.mean((a - truth) ** 2)))
    print(f"fixture: rms(observed - truth) {rms(observed):.4e}, rms(C# estimate - truth) {rms(cs_estimate):.4e}, "
          f"clamped fraction {meta['clampedFraction']:.3g}")
    print("PARITY OK" if ok else "PARITY FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
