"""E3: the unrolled Richardson-Lucy operator with the estimator step's kernel, in LINEAR units.

The fork decided 2026-09-07 (docs/plans/deconvolver-training.md, "The fork, decided") builds the
deconvolver as K iterations of the multiplicative Richardson-Lucy update with the kernel the
estimator step reads off the degraded frame, and a small learned residual prior between iterations.
E3.0 is the operator ALONE through the gate; E3.1 adds the prior. This module is the operator.

Three facts fix its shape, each the way the naive version is wrong:

- **The cache is STRETCHED, the physics is LINEAR.** Every tile in `tiles.f16` went through the SAS
  input stretch (`ChunkedNafnetRunner.ApplyInputStretch`: per-channel minimum subtracted, then the
  midtones transfer function with the balance that lands the channel's median on 0.25), and the
  blur was injected BEFORE that stretch, in linear units, with both sides of a pair stretched by the
  TARGET's parameters (the exporter's rule). A blur does not commute with a tone curve, so RL on
  the stretched tile with a linear kernel deconvolves the wrong image. The operator therefore
  UNSTRETCHES (the exact inverse, `MTF(1 - beta, y) + min`), iterates in linear, and RESTRETCHES with
  the same parameters, which keeps the gate's measurement in the domain every earlier arm was read
  in. The C# oracle ceiling (`DeconvolutionOracleCeilingProbe`) was measured in linear on master
  crops, so this is also the only domain in which E3.0's prediction ("the oracle at a 1.38x input
  reads about 1.03") is a statement about the same operator.
- **The stretch parameters are per SESSION per channel and are recorded nowhere.** The exporter
  measures them once on the clean master and stretches every degraded cell with them
  (`Image.MtfStretchWith`), but neither manifest carries them. `session_stretch_params` recomputes
  them from the retained master the way the exporter did (unit divisor = the frame's observed peak,
  per-channel minimum, median of the shifted channel, `MidtonesBalanceFor(median, 0.25)`), and
  `--prepare` PROVES them against the cache's own slot-0 tiles before writing `stretch.npy`: a
  parameter set that does not reproduce the stored bytes is refused, not stored.
- **The kernel is the row's, per tile, and mirrors `PsfKernel.Build`.** Circular Moffat from
  `EstimatedKernelFwhmPx` / `EstimatedKernelBeta` (the drawn kernel's effective width and beta where
  the estimate was refused, which is the whole-frame fallback inference will use), at the radius
  rule of the C# kernel (`min(6 fwhm, fwhm (1 + 4 / beta))`, ceiling, at least 1) so a torch kernel
  and a C# kernel of the same parameters have the same support and the same weights. The RL update
  itself mirrors `RichardsonLucy.Deconvolve` line for line: replicate border (the C# clamps
  indices), a 1e-12 denominator floor with the ratio defaulting to 1, the adjoint as the flipped
  kernel, a non-finite or negative estimate clamped to zero. `n2n_rl_parity.py` pins the port
  against a fixture the C# writes.

Nothing here estimates a kernel. The estimate was taken by the exporter (`--estimate-kernels`) on
the linear windows, the only place the tone curve had not yet moved the half-maximum crossing.
"""
import json
import os

import numpy as np
import torch
import torch.nn.functional as F

TARGET_MEDIAN = 0.25          # AiNafnetInputs.TargetMedian
DENOMINATOR_FLOOR = 1e-12     # RichardsonLucy.DenominatorFloor
FWHM_PER_SIGMA = 2.3548200450309493

# Label planes appended after the image planes by `n2n_deconv_gate.with_psf01`, in this order.
# Plane 0 is the psf01 conditioning label every deconvolution arm already carries (the U-Net reads
# it, the operator ignores it); the rest are the operator's own. Stated once so the trainer that
# packs them and the module that unpacks them cannot disagree.
LABEL_PSF01 = 0
LABEL_KERNEL_FWHM = 1
LABEL_KERNEL_BETA = 2
LABEL_MIN = slice(3, 6)
LABEL_BETA = slice(6, 9)
LABEL_COUNT = 9

KERNEL_COLUMNS = ("EstimatedKernelFwhmPx", "EstimatedKernelBeta", "EffectiveKernelFwhmPx",
                  "SourceEstimated", "ExtraFwhmPx", "BlurRatioDrawn")
STRETCH_COLUMNS = ("min_r", "min_g", "min_b", "beta_r", "beta_g", "beta_b")
KERNELS_FILE = "kernels.npy"
STRETCH_FILE = "stretch.npy"


# --------------------------------------------------------------------------- the midtones transfer function
def mtf(beta, x):
    """PixInsight's midtones transfer function, `Image.MidtonesTransferFunction`: x clamped to [0, 1]
    (MTF(b, 0) = 0 and MTF(b, 1) = 1, so clamping first is the same as the C# branch)."""
    xc = x.clamp(0.0, 1.0) if torch.is_tensor(x) else np.clip(x, 0.0, 1.0)
    return (beta - 1.0) * xc / ((2.0 * beta - 1.0) * xc - beta)


def midtones_balance_for(orig_median, target=TARGET_MEDIAN):
    """`Image.MidtonesBalanceFor`: the beta with MTF(beta, orig_median) == target."""
    return orig_median * (target - 1.0) / (target * (2.0 * orig_median - 1.0) - orig_median)


def unstretch(y, mins, betas):
    """`Image.MtfUnstretch` on [B, C, H, W]: MTF(1 - beta_c, y) + min_c, per channel."""
    b = betas.view(betas.shape[0], -1, 1, 1)
    m = mins.view(mins.shape[0], -1, 1, 1)
    return mtf(1.0 - b, y) + m


def restretch(lin, mins, betas):
    """`Image.MtfStretchWith` on [B, C, H, W]: MTF(beta_c, max(lin - min_c, 0))."""
    b = betas.view(betas.shape[0], -1, 1, 1)
    m = mins.view(mins.shape[0], -1, 1, 1)
    return mtf(b, (lin - m).clamp_min(0.0))


# --------------------------------------------------------------------------- the kernel
def moffat_radius(fwhm, beta):
    """`PsfKernel.Build`'s support: a Moffat's wings run further the lighter the beta, capped at six
    widths; a Gaussian (beta = inf) is gone by three sigma."""
    if np.isinf(beta):
        radius_f = 3.0 * fwhm / FWHM_PER_SIGMA
    else:
        radius_f = min(6.0 * fwhm, fwhm * (1.0 + 4.0 / beta))
    return max(1, int(np.ceil(radius_f)))


def moffat_kernel(fwhm, beta, radius=None):
    """A normalised circular Moffat (or Gaussian at beta = inf) on the C# kernel's own grid, float64.

    `radius` pads the kernel with zeros to a larger common support (a batch convolves every sample
    at one kernel size); it never shrinks it. Weights are `(1 + r^2 / alpha^2)^-beta` with
    `alpha = fwhm / (2 sqrt(2^(1/beta) - 1))`, exactly `PsfKernel.Build` with elongation 1.
    """
    if not (fwhm > 0):
        raise ValueError(f"kernel fwhm must be positive, got {fwhm}")
    if not (beta > 0):
        raise ValueError(f"kernel beta must be positive, got {beta}")
    r0 = moffat_radius(fwhm, beta)
    r = r0 if radius is None else max(int(radius), r0)
    ky, kx = np.mgrid[-r0:r0 + 1, -r0:r0 + 1].astype(np.float64)
    r2 = kx * kx + ky * ky
    if np.isinf(beta):
        sigma = fwhm / FWHM_PER_SIGMA
        w = np.exp(-r2 / (2.0 * sigma * sigma))
    else:
        alpha = fwhm / (2.0 * np.sqrt(2.0 ** (1.0 / beta) - 1.0))
        w = np.power(1.0 + r2 / (alpha * alpha), -beta)
    w = w * (1.0 / w.sum())
    if r > r0:
        w = np.pad(w, r - r0)
    return w


def kernel_batch(fwhms, betas):
    """[B, k, k] float32 kernels at one common support (the batch's largest), one per sample."""
    fwhms = np.asarray(fwhms, dtype=np.float64)
    betas = np.asarray(betas, dtype=np.float64)
    radius = max(moffat_radius(f, b) for f, b in zip(fwhms, betas))
    return np.stack([moffat_kernel(f, b, radius) for f, b in zip(fwhms, betas)]).astype(np.float32)


# --------------------------------------------------------------------------- the iteration
class StretchedPrior(torch.nn.Module):
    """E3.1's learned prior: a residual network applied to the estimate BETWEEN iterations, in the
    stretched domain, and the identity at initialisation.

    Stretched, because a CNN conditioned on linear astronomical values (a background at 0.005 under
    peaks at 1) sees three decades in one plane, while the stretched tile is the domain every earlier
    arm trained in and the gate measures in. Identity at step 0 (the output convolution is zeroed),
    so an untrained E3.1 IS E3.0 and "did the prior help" is read against a known start rather than
    against noise. A pixel whose stretched value is saturated (linear at or over the stretch's
    ceiling) passes through untouched: the tone curve has no inverse there, and clipping a sharpened
    core once per iteration would cap the brightest stars.
    """

    def __init__(self, net, every=1):
        super().__init__()
        self.net = net
        self.every = max(1, int(every))
        out = getattr(net, "out", None)
        if out is not None:
            torch.nn.init.zeros_(out.weight)
            if out.bias is not None:
                torch.nn.init.zeros_(out.bias)

    def forward(self, estimate, iteration, mins, betas):
        if (iteration + 1) % self.every:
            return estimate
        stretched = restretch(estimate, mins, betas)
        corrected = self.net(stretched).clamp(0.0, 1.0)
        saturated = stretched >= 1.0
        return torch.where(saturated, estimate, unstretch(corrected, mins, betas))


def rl_deconvolve(observed, kernels, iterations, prior=None, checkpoint=None, recompute=False):
    """`RichardsonLucy.Deconvolve` on a batch: `u <- u * (P^T [d / (P u)])`, K times.

    observed: [B, C, H, W] LINEAR, non-negative (a negative or non-finite sample is clamped to zero,
    as the C# does; the caller reads how many were). kernels: [B, k, k], one per SAMPLE and shared
    across its channels, at one common odd size. prior: optional callable applied to the estimate
    after every update (E3.1's learned residual; None is E3.0). checkpoint: optional callable
    `(iteration, estimate)` after every update, the C# `CheckpointHandler`.

    Cross-correlation with the kernel as stored (the C# `Convolve` reads `src[y + ky, x + kx] *
    w[ky, kx]`, which is what `conv2d` computes) and with the FLIPPED kernel as the adjoint (the C#
    `Mirrored()`), replicate padding for the C# index clamp, the ratio defaulting to 1 where the
    blurred estimate is under the floor. The division is taken on the floored denominator and then
    masked, rather than masked after a raw division, so no `inf` ever enters an autograd graph.
    """
    if observed.dim() != 4:
        raise ValueError(f"observed must be [B, C, H, W], got {tuple(observed.shape)}")
    B, C, H, W = observed.shape
    kernels = torch.as_tensor(kernels, device=observed.device, dtype=observed.dtype)
    if kernels.shape[0] != B or kernels.shape[-1] != kernels.shape[-2] or kernels.shape[-1] % 2 == 0:
        raise ValueError(f"kernels must be [B, k, k] with k odd, got {tuple(kernels.shape)} for B={B}")
    k = kernels.shape[-1]
    r = k // 2
    data = torch.where(torch.isfinite(observed) & (observed >= 0), observed, torch.zeros_like(observed))
    weight = kernels[:, None].expand(B, C, k, k).reshape(B * C, 1, k, k)
    adjoint = torch.flip(weight, dims=(-1, -2))

    def convolve(t, w):
        padded = F.pad(t.reshape(1, B * C, H, W), (r, r, r, r), mode="replicate")
        return F.conv2d(padded, w, groups=B * C).reshape(B, C, H, W)

    def step(estimate, it):
        blurred = convolve(estimate, weight)
        ratio = torch.where(blurred > DENOMINATOR_FLOOR,
                            data / blurred.clamp_min(DENOMINATOR_FLOOR),
                            torch.ones_like(blurred))
        correction = convolve(ratio, adjoint)
        nxt = estimate * correction
        nxt = torch.where(torch.isfinite(nxt) & (nxt > 0), nxt, torch.zeros_like(nxt))
        if prior is not None:
            nxt = prior(nxt, it)
        return nxt

    estimate = data
    for it in range(iterations):
        if recompute and torch.is_grad_enabled():
            # Training: K iterations of a network on a batch of tiles would hold K sets of
            # activations; re-running each iteration in the backward pass keeps only the K estimates.
            from torch.utils.checkpoint import checkpoint as ckpt
            estimate = ckpt(step, estimate, it, use_reentrant=False)
        else:
            estimate = step(estimate, it)
        if checkpoint is not None:
            checkpoint(it + 1, estimate)
    return estimate


class RLOperator(torch.nn.Module):
    """The operator as the gate and the trainer see a model: stretched tile in, stretched tile out.

    Takes the image planes plus the LABEL planes `with_psf01` appends (see the LABEL_* constants),
    unstretches with the tile's own session parameters, runs `rl_deconvolve` with the tile's own
    kernel, restretches. `prior` is E3.1's residual network between iterations; E3.0 passes None and
    the module then has no parameters at all, which is the point of E3.0.
    """

    def __init__(self, iterations, prior=None, image_planes=3, recompute=True):
        super().__init__()
        self.iterations = int(iterations)
        self.prior = prior          # a StretchedPrior, or None for E3.0
        self.image_planes = image_planes
        # Re-run each iteration in the backward pass instead of holding its activations. ON by
        # default, and measured before it was decided (2026-09-13, batch 8, K = 20, base-16 prior,
        # the 1070): held activations peak at 11.1 GB, past the card, and the step takes 31.8 s as
        # the driver pages; recomputed, the peak is 0.76 GB and the step 3.7 s. The recompute is
        # not a trade here, it is the only way the step fits.
        self.recompute = recompute

    def forward(self, x):
        img = x[:, :self.image_planes]
        labels = x[:, self.image_planes:, 0, 0]
        if labels.shape[1] < LABEL_COUNT:
            raise ValueError(f"the operator needs {LABEL_COUNT} label planes, got {labels.shape[1]}; "
                             f"prepare the cache with --bake so stretch.npy and kernels.npy exist")
        kernels = kernel_batch(labels[:, LABEL_KERNEL_FWHM].detach().cpu().numpy(),
                               labels[:, LABEL_KERNEL_BETA].detach().cpu().numpy())
        mins = labels[:, LABEL_MIN]
        betas = labels[:, LABEL_BETA]
        linear = unstretch(img, mins, betas)
        prior = None
        if self.prior is not None:
            prior = lambda est, it: self.prior(est, it, mins, betas)
        estimate = rl_deconvolve(linear, kernels, self.iterations, prior=prior,
                                 recompute=self.recompute and self.training and self.prior is not None)
        return restretch(estimate, mins, betas)


# --------------------------------------------------------------------------- the session's stretch
def sanitize(session_id):
    """`DatasetTileExporter.Sanitize`: the characters a file name cannot carry become underscores."""
    return "".join("_" if ch in '/\\|:*?"<>' else ch for ch in session_id)


def read_master(bake_root, session_id):
    """The retained master's planes [C, H, W] float32, from the first HDU carrying an image."""
    from astropy.io import fits
    path = os.path.join(bake_root, "session-masters", sanitize(session_id) + ".fits")
    with fits.open(path, memmap=False) as hdus:
        hdu = next((h for h in hdus if h.data is not None), None)
        if hdu is None:
            raise FileNotFoundError(f"no image HDU in {path}")
        data = np.asarray(hdu.data, dtype=np.float32)
        header = hdu.header
    if data.ndim == 2:
        data = data[None]
    return data, header, path


def session_stretch_params(master, divisor):
    """(mins[C], betas[C]) exactly as `DatasetTileExporter.ToUnitRange` + `Image.MtfStretch` derive
    them: unit = master * (1 / divisor) in float32, per channel the NaN-skipped minimum, the median
    of the shifted channel, `MidtonesBalanceFor(median, 0.25)` (0.5, the identity, on a flat plane)."""
    inv = np.float32(1.0) / np.float32(divisor) if divisor != 1.0 else np.float32(1.0)
    unit = master * inv if divisor != 1.0 else master
    mins = np.zeros(unit.shape[0], dtype=np.float32)
    betas = np.zeros(unit.shape[0], dtype=np.float64)
    for c in range(unit.shape[0]):
        plane = unit[c]
        finite = plane[np.isfinite(plane)]
        mn = np.float32(finite.min()) if finite.size else np.float32(0.0)
        mins[c] = mn
        shifted = (finite - mn).astype(np.float32)
        med = float(np.median(shifted)) if shifted.size else 0.0
        betas[c] = midtones_balance_for(med) if med > 0.0 else 0.5
    return mins, betas


def stretch_crop(master, divisor, mins, betas, cell_x, cell_y, tile):
    """The exporter's clean tile for one cell, in float16, from the master and the parameters:
    unit, shift, MTF per channel, NaN to zero (`ExtractTileHalfs`), rounded to half."""
    crop = master[:, cell_y:cell_y + tile, cell_x:cell_x + tile]
    inv = np.float32(1.0) / np.float32(divisor) if divisor != 1.0 else np.float32(1.0)
    unit = (crop * inv).astype(np.float32) if divisor != 1.0 else crop.astype(np.float32)
    out = np.empty_like(unit)
    for c in range(unit.shape[0]):
        shifted = np.clip((unit[c] - mins[c]).astype(np.float32), 0.0, None)
        y = mtf(betas[c], shifted.astype(np.float64)).astype(np.float32)
        y[~np.isfinite(unit[c])] = 0.0
        out[c] = y
    return out.astype(np.float16)


def prove_stretch_params(master, header, divisor_candidates, mins_by, cells, read_tile, tile, max_cells=3):
    """Pick the unit divisor that reproduces the cache's own clean tiles, and report how well.

    `UnitDivisor` is `max(image.MaxValue, data max)` and a FITS-read master's MaxValue is the
    observed peak, so the data max is the expected answer; the header's DATAMAX is tried second in
    case a reader ever stamped it instead. Returns (divisor, mins, betas, worst max-abs half diff,
    worst median-abs diff) for the first candidate whose worst tile agrees to within two half ulps
    at the stretch target, or raises naming the best it saw.
    """
    best = None
    for divisor in divisor_candidates:
        mins, betas = mins_by(divisor)
        worst_max, worst_med = 0.0, 0.0
        for (cx, cy, rel) in cells[:max_cells]:
            mine = stretch_crop(master, divisor, mins, betas, cx, cy, tile).astype(np.float32)
            theirs = read_tile(rel).astype(np.float32)
            diff = np.abs(mine - theirs)
            worst_max = max(worst_max, float(diff.max()))
            worst_med = max(worst_med, float(np.median(diff)))
        if best is None or worst_max < best[3]:
            best = (divisor, mins, betas, worst_max, worst_med)
        # Two half ulps at 0.25 (ulp 2.44e-4): the stretch and the cache round the same doubles to
        # the same halves, so anything past a rounding-tie difference is a parameter difference.
        if worst_max <= 5e-4:
            return divisor, mins, betas, worst_max, worst_med
    divisor, _, _, worst_max, worst_med = best
    raise SystemExit(f"stretch parameters do not reproduce the cache's clean tiles: best divisor "
                     f"{divisor:g} leaves max |diff| {worst_max:.3e} (median {worst_med:.3e}); "
                     f"the operator would deconvolve in the wrong units, so this refuses")
