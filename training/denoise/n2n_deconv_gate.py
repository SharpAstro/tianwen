"""Selection metrics for a DECONVOLVER, because the denoiser's gate would select for blurring.

`n2n_gate.Gate` thresholds on residual noise, faint-star amplitude and fabricated point sources.
Every one of those is noise-oriented, and none of them is about sharpness: run it over a
deconvolution arm and it happily picks whichever checkpoint irons the frame flattest, which is the
opposite of the job. That is not a tuning difference, it is the wrong objective, and it would have
produced a confident number nothing downstream could read as wrong.

What a deconvolver is selected on instead, and why each one is here:

- **FWHM ratio**, recovered over truth. The thing being asked for. It has a floor as well as a
  target: `docs/plans/deconvolver-training.md` H1 measured that an oracle handed the EXACT kernel
  never produces a star narrower than the one that was there, so a ratio under 1 is fabrication and
  not success.
- **Ring excess**, over the same statistic measured on this arm's own INPUT. The raw annulus
  minimum is not usable on its own: the minimum of about a hundred noise samples sits ~2.5 sigma
  below the mean before anything is deconvolved, so it reports ringing on an untouched frame. Only
  the excess over that null is the iteration's doing. (Learned the expensive way on the C# probe,
  which read 1.0 MAD on half the stars of a 0.5 px blur.)
- **Stars kept**, detections over the truth's. Deconvolution on noisy data sharpens noise into
  point sources, and a detector reads those as narrow stars, so an FWHM number can improve by
  inventing a population. Twice today a metric was flattered exactly that way; the count is what
  caught it both times, so it travels beside the width rather than in a footnote.

The oracle ceiling those are read against, at 60 RL iterations: full recovery to 1.3x blur, within
10 percent of the truth width to 2x, about 1.6x beyond. An arm that beats the ceiling on comparable
pairs is fabricating, because the oracle was handed the one thing inference never has.

Run `python n2n_deconv_gate.py --self-test` to check the width estimator against known answers. It
is the piece everything else rests on and it is not obvious by inspection.
"""
import numpy as np
import torch
from scipy.ndimage import binary_dilation, maximum_filter

import n2n_metrics as M
import n2n_smoke as S

# Detection bar for the star set the width is measured over. Higher than the gate's 8 MAD because a
# width needs a profile, not just a peak: a marginal detection's half-maximum crossing is noise.
STAR_SIGMA = 12.0

# Annulus for the ring statistic, in multiples of the measured truth width.
RING_INNER = 1.2
RING_OUTER = 2.5


def with_psf01(x, psf01):
    """Append the psf01 conditioning plane, mirroring `n2n_smoke.with_sigma`'s shape.

    The difference from the denoiser's plane is the whole point of P2's H2: `with_sigma` MEASURES
    the input's noise, while this is a stored label the exporter wrote by running the deployed
    estimator on the degraded frame. Inference has no kernel, so the label has to be a quantity the
    deployed path can obtain; a plane derived from the drawn kernel parameters would not be.
    """
    s = torch.as_tensor(psf01, device=x.device, dtype=torch.float32).view(-1, 1, 1, 1)
    return torch.cat([x, s.expand(-1, -1, x.shape[2], x.shape[3])], dim=1)


def star_fwhm(tile, ys, xs, med, max_r=10):
    """Median FWHM in pixels over the given star positions, by radial half-maximum crossing.

    Four directions per star, averaged, then a median over stars: a single direction is hostage to
    a neighbour or a hot column, and the mean over stars is hostage to the one saturated star in
    the field. Stars whose profile does not fall to half within `max_r` are dropped rather than
    reported at the walk limit, which would silently pile up at a constant.
    """
    h, w = tile.shape
    widths = []
    for y, x in zip(ys, xs):
        peak = tile[y, x] - med
        if peak <= 0:
            continue

        half = peak * 0.5
        crossings = []
        for dy, dx in ((0, 1), (0, -1), (1, 0), (-1, 0)):
            prev = peak
            for r in range(1, max_r + 1):
                yy, xx = y + (dy * r), x + (dx * r)
                if not (0 <= yy < h and 0 <= xx < w):
                    break

                here = tile[yy, xx] - med
                if here <= half:
                    # Linear interpolation between the two samples straddling the half maximum.
                    t = (prev - half) / (prev - here) if prev > here else 0.0
                    crossings.append(r - 1 + t)
                    break

                prev = here

        if len(crossings) == 4:
            widths.append(2.0 * float(np.mean(crossings)))

    return float(np.median(widths)) if widths else float("nan")


def ring_excess(tile, ys, xs, fwhm, med, mad):
    """Fraction of stars whose annulus minimum sits more than 1 MAD below the local background.

    Returned RAW; the caller subtracts the same statistic measured on the input, because this reads
    well above zero on a frame nothing has been done to.
    """
    if not np.isfinite(fwhm) or fwhm <= 0 or mad <= 0:
        return float("nan")

    h, w = tile.shape
    inner, outer = RING_INNER * fwhm, RING_OUTER * fwhm
    r = int(np.ceil(outer))
    yy, xx = np.mgrid[-r:r + 1, -r:r + 1]
    dist = np.sqrt((yy * yy) + (xx * xx))
    ring = (dist >= inner) & (dist <= outer)
    if not ring.any():
        return float("nan")

    over = 0
    n = 0
    for y, x in zip(ys, xs):
        if y - r < 0 or x - r < 0 or y + r >= h or x + r >= w:
            continue

        patch = tile[y - r:y + r + 1, x - r:x + r + 1]
        n += 1
        if (med - patch[ring].min()) > mad:
            over += 1

    return float(over) / n if n else float("nan")


class DeconvGate:
    """Mid-training probe for a deconvolution arm, mirroring `n2n_gate.Gate`'s shape.

    cells: absolute cache indices to probe. Keep it small; this runs inside the training loop.
    psf01: the stored conditioning label for those cells, in cache order.
    input_slot: the DEGRADED draw the model is applied to. Slot 0 is the clean target.
    """

    def __init__(self, mm, cells, device, psf01, input_slot=1):
        self.dev = device
        self.psf01 = np.asarray(psf01, dtype=np.float32)
        self.masters = np.asarray(mm[cells, S.SLOT_MASTER], dtype=np.float32)
        self.degraded = np.asarray(mm[cells, input_slot], dtype=np.float32)
        if not np.any(self.degraded):
            raise SystemExit(f"deconv gate input slot {input_slot} is all zeros on this cache")

        if len(self.psf01) != len(cells):
            raise SystemExit(f"psf01 has {len(self.psf01)} entries for {len(cells)} cells; the "
                             f"cache's label column and its cell list have to be the same length")

        self.lm = S.crop(self.masters).mean(axis=1)
        self.li = S.crop(self.degraded).mean(axis=1)

        # The star set is fixed ON THE TRUTH and reused for every arm, so a width or a ring count
        # can never partly measure which stars a given output happened to keep.
        self.stars = []
        self.truth_fwhm = []
        self.truth_stats = []
        for t in self.lm:
            med = float(np.median(t))
            _, mad = M.bg_stats(t)
            det = (t >= maximum_filter(t, size=5)) & (t > med + STAR_SIGMA * mad)
            ys, xs = np.nonzero(det)
            ok = (ys > 3) & (ys < t.shape[0] - 4) & (xs > 3) & (xs < t.shape[1] - 4)
            ys, xs = ys[ok], xs[ok]
            self.stars.append((ys, xs))
            self.truth_stats.append((med, mad))
            self.truth_fwhm.append(star_fwhm(t, ys, xs, med))

        self.truth_fwhm = np.array(self.truth_fwhm, dtype=np.float64)
        self.truth_stars = np.array([len(s[0]) for s in self.stars], dtype=np.float64)

        # The ring NULL, measured on the input each arm actually receives, so the excess reported
        # below is the model's doing and not the noise floor's.
        self.ring_null = np.array([
            ring_excess(self.li[i], self.stars[i][0], self.stars[i][1],
                        self.truth_fwhm[i], *self.truth_stats[i])
            for i in range(len(self.lm))
        ], dtype=np.float64)
        self.input_fwhm = np.array([
            star_fwhm(self.li[i], self.stars[i][0], self.stars[i][1], self.truth_stats[i][0])
            for i in range(len(self.lm))
        ], dtype=np.float64)

    def _forward(self, model, src):
        out = []
        with torch.no_grad():
            for i in range(0, len(src), 8):
                x = torch.from_numpy(src[i:i + 8]).to(self.dev)
                out.append(model(with_psf01(x, self.psf01[i:i + 8])).cpu().numpy())
        return np.concatenate(out)

    def evaluate(self, model):
        """One probe. Returns the numbers a deconvolution run should be selected on."""
        was_training = model.training
        model.eval()
        try:
            den = S.crop(self._forward(model, self.degraded)).mean(axis=1)
        finally:
            if was_training:
                model.train()

        ratios, residuals, excess, kept = [], [], [], []
        for i in range(len(den)):
            ys, xs = self.stars[i]
            med, mad = self.truth_stats[i]
            truth = self.truth_fwhm[i]
            if not np.isfinite(truth) or truth <= 0:
                continue

            w = star_fwhm(den[i], ys, xs, med)
            if np.isfinite(w):
                ratios.append(w / truth)
                residuals.append(w - truth)

            e = ring_excess(den[i], ys, xs, truth, med, mad)
            if np.isfinite(e) and np.isfinite(self.ring_null[i]):
                excess.append(e - self.ring_null[i])

            out_det = (den[i] >= maximum_filter(den[i], size=5)) & (den[i] > med + STAR_SIGMA * mad)
            if self.truth_stars[i] > 0:
                kept.append(float(out_det.sum()) / self.truth_stars[i])

        def med_of(v):
            return float(np.median(v)) if v else float("nan")

        return {
            "fwhm_ratio": med_of(ratios),
            "residual_px": med_of(residuals),
            "ring_excess": med_of(excess),
            "stars_kept": med_of(kept),
            "input_ratio": float(np.nanmedian(self.input_fwhm / self.truth_fwhm)),
        }

    @staticmethod
    def header():
        return (f"{'in/truth':>9} {'out/truth':>10} {'resid px':>9} {'ring exc':>9} {'stars':>6}")

    @staticmethod
    def format(m):
        return (f"{m['input_ratio']:9.3f} {m['fwhm_ratio']:10.3f} {m['residual_px']:+9.3f} "
                f"{m['ring_excess']:+9.1%} {m['stars_kept']:6.2f}")


def _self_test():
    """Check the width estimator against answers known in advance.

    It is the piece every metric above rests on, it is not obvious by inspection, and an estimator
    that is quietly wrong would make every arm's number wrong in the same direction, which is the
    hardest kind of error to notice.
    """
    rng = np.random.default_rng(7)
    size = 128
    ok = True

    def plate(fwhm, n_stars=24, noise=0.0):
        img = np.full((size, size), 0.10, dtype=np.float32)
        sigma = fwhm / 2.3548200450309493
        ys = rng.integers(12, size - 12, n_stars)
        xs = rng.integers(12, size - 12, n_stars)
        yy, xx = np.mgrid[0:size, 0:size]
        for y, x in zip(ys, xs):
            img += np.exp(-(((yy - y) ** 2) + ((xx - x) ** 2)) / (2 * sigma * sigma)).astype(np.float32)
        if noise > 0:
            img = img + rng.normal(0, noise, img.shape).astype(np.float32)
        return img

    for want in (2.0, 3.0, 4.5):
        img = plate(want)
        med = float(np.median(img))
        _, mad = M.bg_stats(img)
        det = (img >= maximum_filter(img, size=5)) & (img > med + STAR_SIGMA * mad)
        ys, xs = np.nonzero(det)
        keep = (ys > 3) & (ys < size - 4) & (xs > 3) & (xs < size - 4)
        got = star_fwhm(img, ys[keep], xs[keep], med)
        err = abs(got - want) / want
        flag = "ok" if err < 0.10 else "FAIL"
        ok &= err < 0.10
        print(f"  width {want:4.1f} px -> measured {got:5.2f} ({err:5.1%} off)  {flag}")

    # A wider plate must read wider, which is the only property the gate's ORDERING needs.
    narrow, wide = plate(2.0), plate(3.5)
    mn, mw = float(np.median(narrow)), float(np.median(wide))
    dn = (narrow >= maximum_filter(narrow, size=5)) & (narrow > mn + STAR_SIGMA * M.bg_stats(narrow)[1])
    dw = (wide >= maximum_filter(wide, size=5)) & (wide > mw + STAR_SIGMA * M.bg_stats(wide)[1])
    yn, xn = np.nonzero(dn)
    yw, xw = np.nonzero(dw)
    a = star_fwhm(narrow, yn, xn, mn)
    b = star_fwhm(wide, yw, xw, mw)
    print(f"  ordering: {a:.2f} px < {b:.2f} px  {'ok' if a < b else 'FAIL'}")
    ok &= a < b

    # The ring statistic must read NEAR ZERO on an untouched noisy plate: this is the null that
    # made the raw annulus minimum unusable, and the reason the gate reports an excess.
    noisy = plate(3.0, noise=0.02)
    mnz = float(np.median(noisy))
    _, madz = M.bg_stats(noisy)
    dz = (noisy >= maximum_filter(noisy, size=5)) & (noisy > mnz + STAR_SIGMA * madz)
    yz, xz = np.nonzero(dz)
    kz = (yz > 12) & (yz < size - 13) & (xz > 12) & (xz < size - 13)
    raw = ring_excess(noisy, yz[kz], xz[kz], 3.0, mnz, madz)
    print(f"  ring on an untouched noisy plate: {raw:.1%} of stars "
          f"(NOT expected to be 0; this is why the gate subtracts a null)")

    print("self-test PASSED" if ok else "self-test FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    import sys
    if "--self-test" in sys.argv:
        raise SystemExit(_self_test())
    print(__doc__)
