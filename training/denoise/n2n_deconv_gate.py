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

# A second, LOWER bar for the star COUNT only, reported beside the 12 MAD one and never selected on.
# E2.8 trains a term on the stars `detect()` finds at STAR_SIGMA, and a net could satisfy it by
# sharpening exactly those while suppressing the population underneath; a count at half the bar is
# what would show that. Report-only: nobody has measured what value it should read.
STAR_SIGMA_LOW = 6.0

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
    s = torch.as_tensor(psf01, device=x.device, dtype=torch.float32)
    # One label per sample ([B]) is the psf01 plane every deconvolution arm has carried; a ROW per
    # sample ([B, L]) is the E3 operator's label set (n2n_operator.LABEL_*), one constant plane each.
    # The one-label form is byte-identical to what it was.
    s = s.view(-1, 1, 1, 1) if s.dim() == 1 else s.view(s.shape[0], s.shape[1], 1, 1)
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


def detect(tile, med, mad, margin=4, sigma=STAR_SIGMA):
    """Peak detections on one tile, with the SAME edge margin everywhere it is used.

    Factored out because it was inlined twice with different margins, and that asymmetry is a bug
    with no symptom: the truth's star set was edge-trimmed (`ys > 3`, and the interior of a 5-px
    maximum filter needs it) while the output count was a bare `det.sum()` over the whole tile
    including the rim. The two therefore covered different AREAS, and the truth scored 1.096 against
    ITSELF where a self-consistency null has to be 1.000. Every stars_kept ever printed by this gate
    before 2026-09-06 is inflated by about a tenth for that reason.

    `sigma` exists for the report-only low-bar count (STAR_SIGMA_LOW); the width and the loss both
    stay on STAR_SIGMA.
    """
    det = (tile >= maximum_filter(tile, size=5)) & (tile > med + sigma * mad)
    ys, xs = np.nonzero(det)
    ok = ((ys >= margin) & (ys < tile.shape[0] - margin)
          & (xs >= margin) & (xs < tile.shape[1] - margin))
    return ys[ok], xs[ok]


# The star-term loss reads a 7x7 window per star, so a star needs 3 px of tile on every side; detect()'s
# margin of 4 already guarantees that on the CROPPED tile the loss and the gate both work on.
STAR_TARGET_COLUMNS = ("y", "x", "peak_mad", "valid")


def star_targets(tile, med, mad, max_per_tile=32):
    """The per-tile star set E2.8's star-term loss is trained on: `detect()` on the CLEAN target.

    Returns float32 [max_per_tile, 4] rows of (y, x, peak over median in MAD, valid), zero-padded, in
    the coordinates of the tile handed in (the caller hands the CROPPED luminance, so the loss indexes
    the cropped prediction directly and the positions agree with the gate's).

    The same detector as the gate, deliberately: a loss trained on one star set and a gate judging on
    another would let the net satisfy the loss on stars the gate never looks at. Where a tile holds more
    than `max_per_tile` detections the kept ones are EVENLY SPACED IN PEAK RANK rather than the
    brightest: L2 already looks after the bright stars, and the faint end is the population E2.7
    measured being traded away, so a selection that kept only the bright ones would put the term's
    weight where it is least needed.
    """
    ys, xs = detect(tile, med, mad)
    out = np.zeros((max_per_tile, len(STAR_TARGET_COLUMNS)), dtype=np.float32)
    if len(ys) == 0:
        return out

    peaks = (tile[ys, xs] - med) / mad
    order = np.argsort(-peaks, kind="stable")
    if len(order) > max_per_tile:
        step = len(order) / max_per_tile
        order = order[[int(j * step) for j in range(max_per_tile)]]

    n = len(order)
    out[:n, 0] = ys[order]
    out[:n, 1] = xs[order]
    out[:n, 2] = peaks[order]
    out[:n, 3] = 1.0
    return out


# The loss's 7x7 window (n2n_smoke.STAR_WINDOW_R, restated here so this module imports nothing back
# at load time), and how far the nearest LOW-bar detection must be from an EMPTY window's centre:
# outside the window plus one pixel of slack.
STAR_WINDOW_R = 3
EMPTY_EXCLUSION_R = STAR_WINDOW_R + 1


def empty_targets(tile, med, mad, count, max_per_tile=32, margin=4, seed=0):
    """Windows where the CLEAN target has NO star: the star term's counterpart (E2.8b arm N).

    E2.8's term rewards a matched peak, flux and concentration at detected stars and says nothing
    about the rest of the frame, so the cheapest way to raise a blurred peak is to sharpen every
    peak, and on a quiet frame the sharpened noise clears the detector: 34 to 45 times the truth's
    stars on the observer session. These windows are where the target is empty by its own low-bar
    detector (`STAR_SIGMA_LOW`, so a six-MAD bump is not "empty"), placed at random with a seeded
    generator so the cache is reproducible, `count` of them to match the tile's star count. The loss
    applies the same three ratios here, where the target's flux and peak are at the noise floor, so a
    peak the output raises over empty sky is penalised the way a lowered star peak is.

    Same layout as `star_targets` (y, x, peak over median in MAD, valid), zero-padded.
    """
    rng = np.random.default_rng(seed)
    h, w = tile.shape
    out = np.zeros((max_per_tile, len(STAR_TARGET_COLUMNS)), dtype=np.float32)
    want = min(count, max_per_tile)
    if want <= 0:
        return out

    lo_ys, lo_xs = detect(tile, med, mad, margin=0, sigma=STAR_SIGMA_LOW)
    bar = med + STAR_SIGMA_LOW * mad
    n = 0
    for _ in range(want * 60):
        if n >= want:
            break
        y = int(rng.integers(margin, h - margin))
        x = int(rng.integers(margin, w - margin))
        if lo_ys.size and np.min(np.maximum(np.abs(lo_ys - y), np.abs(lo_xs - x))) <= EMPTY_EXCLUSION_R:
            continue
        win = tile[y - STAR_WINDOW_R:y + STAR_WINDOW_R + 1, x - STAR_WINDOW_R:x + STAR_WINDOW_R + 1]
        if win.max() > bar:
            continue
        out[n] = (y, x, (float(win.max()) - med) / mad, 1.0)
        n += 1
    return out


def ring_excess(tile, ys, xs, fwhm, med, mad):
    """Median annulus DEPTH below background, in MAD, over the given stars.

    Returned RAW; the caller subtracts the same statistic measured on the input, because this reads
    well above zero on a frame nothing has been done to.

    **A depth and not a fraction-over-a-threshold, which is what this was first.** The thresholded
    form ("what share of stars undershoot by more than 1 MAD") saturates on real data: the minimum of
    about a hundred noise samples is already past 1 MAD before anything is deconvolved, so input and
    output both read 1.0 and the excess is exactly 0.0 on every row. Seen on the first real run, where
    the column printed +0.0% for the selector AND both observers at every probe. A column that cannot
    move is worse than no column, because it is later read as "no ringing observed".
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

    depths = []
    for y, x in zip(ys, xs):
        if y - r < 0 or x - r < 0 or y + r >= h or x + r >= w:
            continue

        patch = tile[y - r:y + r + 1, x - r:x + r + 1]
        depths.append((med - patch[ring].min()) / mad)

    return float(np.median(depths)) if depths else float("nan")


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
        truth_stars_lo = []
        for t in self.lm:
            med = float(np.median(t))
            _, mad = M.bg_stats(t)
            ys, xs = detect(t, med, mad)
            self.stars.append((ys, xs))
            self.truth_stats.append((med, mad))
            self.truth_fwhm.append(star_fwhm(t, ys, xs, med))
            truth_stars_lo.append(len(detect(t, med, mad, sigma=STAR_SIGMA_LOW)[0]))

        self.truth_fwhm = np.array(self.truth_fwhm, dtype=np.float64)
        self.truth_stars = np.array([len(s[0]) for s in self.stars], dtype=np.float64)
        self.truth_stars_lo = np.array(truth_stars_lo, dtype=np.float64)

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

        # The star NULL, on the same footing as the ring null above and for the same reason. Without
        # it `stars_kept` was compared against a band centred on 1.0 as though the model's INPUT were
        # lossless, when the blur is exactly what pushes faint stars under the detection threshold.
        # Measured on this project's own cache the input scores 0.763, so the old [0.90, 1.10] band
        # placed "reproduced the input exactly" OUTSIDE it and no arm could pass at any step: six
        # seeds and 240 probes failed, every one of them for landing at 0.77 to 0.82, which IS the
        # input's own value. A criterion whose null sits outside its pass band cannot be met, and
        # reads in the log as the model failing rather than the gate.
        null = []
        null_lo = []
        for i in range(len(self.lm)):
            if self.truth_stars[i] > 0:
                iy, ix = detect(self.li[i], *self.truth_stats[i])
                null.append(len(iy) / self.truth_stars[i])
            if self.truth_stars_lo[i] > 0:
                iy, _ = detect(self.li[i], *self.truth_stats[i], sigma=STAR_SIGMA_LOW)
                null_lo.append(len(iy) / self.truth_stars_lo[i])
        self.stars_null = float(np.median(null)) if null else float("nan")
        self.stars_null_lo = float(np.median(null_lo)) if null_lo else float("nan")

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

        ratios, residuals, excess, kept, kept_lo = [], [], [], [], []
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

            oy, _ = detect(den[i], med, mad)
            if self.truth_stars[i] > 0:
                kept.append(len(oy) / self.truth_stars[i])

            # The low-bar count. Same truth-fixed threshold units, half the bar: a net that keeps
            # the 12 MAD stars while erasing the 6 MAD ones reads fine in `kept` and falls here.
            oy, _ = detect(den[i], med, mad, sigma=STAR_SIGMA_LOW)
            if self.truth_stars_lo[i] > 0:
                kept_lo.append(len(oy) / self.truth_stars_lo[i])

        def med_of(v):
            return float(np.median(v)) if v else float("nan")

        return {
            "fwhm_ratio": med_of(ratios),
            "residual_px": med_of(residuals),
            "ring_excess": med_of(excess),
            "stars_kept": med_of(kept),
            "stars_kept_lo": med_of(kept_lo),
            "input_ratio": float(np.nanmedian(self.input_fwhm / self.truth_fwhm)),
        }

    @staticmethod
    def header():
        return (f"{'in/truth':>9} {'out/truth':>10} {'resid px':>9} {'ring MAD':>9} {'stars':>6} "
                f"{'stars@' + str(int(STAR_SIGMA_LOW)):>8}")

    @staticmethod
    def format(m):
        return (f"{m['input_ratio']:9.3f} {m['fwhm_ratio']:10.3f} {m['residual_px']:+9.3f} "
                f"{m['ring_excess']:+9.2f} {m['stars_kept']:6.2f} {m.get('stars_kept_lo', float('nan')):8.2f}")


def _self_test():
    """Check the width estimator against answers known in advance.

    It is the piece every metric above rests on, it is not obvious by inspection, and an estimator
    that is quietly wrong would make every arm's number wrong in the same direction, which is the
    hardest kind of error to notice.
    """
    rng = np.random.default_rng(7)
    size = 128
    ok = True

    def plate(fwhm, n_stars=24, noise=0.0, amps=None, seed=None):
        # `amps` and `seed` exist for the star-null demo below and default to the original
        # behaviour: equal-amplitude stars at a caller-chosen position draw. Every star being
        # equally bright is fine for measuring a WIDTH, and useless for measuring which stars a
        # blur erases, since none of them is ever near the detection threshold.
        r = np.random.default_rng(seed) if seed is not None else rng
        img = np.full((size, size), 0.10, dtype=np.float32)
        sigma = fwhm / 2.3548200450309493
        ys = r.integers(12, size - 12, n_stars)
        xs = r.integers(12, size - 12, n_stars)
        yy, xx = np.mgrid[0:size, 0:size]
        for k, (y, x) in enumerate(zip(ys, xs)):
            a = 1.0 if amps is None else float(amps[k])
            img += (a * np.exp(-(((yy - y) ** 2) + ((xx - x) ** 2))
                               / (2 * sigma * sigma))).astype(np.float32)
        if noise > 0:
            img = img + r.normal(0, noise, img.shape).astype(np.float32)
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
    print(f"  ring depth on an untouched noisy plate: {raw:.2f} MAD "
          f"(NOT expected to be 0; this is why the gate subtracts a null)")

    # The edge margin has to BITE, and this is the assertion that fails if the numerator and the
    # denominator ever drift apart again. Comparing detect() to itself would be a tautology and
    # would pass with the original bug still in place, because that bug was between an edge-trimmed
    # truth count and an untrimmed `det.sum()` over the output. So plant stars in the rim and check
    # that detect() drops exactly them while the untrimmed count keeps them.
    clean = plate(3.0, noise=0.0)
    mc = float(np.median(clean))
    _, madc = M.bg_stats(clean)
    rim = clean.copy()
    for ry, rx in ((1, size // 2), (size - 2, size // 2), (size // 2, 1), (size // 2, size - 2)):
        rim[ry, rx] = clean.max()
    untrimmed = int(((rim >= maximum_filter(rim, size=5)) & (rim > mc + STAR_SIGMA * madc)).sum())
    trimmed = len(detect(rim, mc, madc)[0])
    base = len(detect(clean, mc, madc)[0])
    print(f"  rim stars: untrimmed count {untrimmed}, detect() {trimmed}, clean baseline {base}  "
          f"{'ok' if trimmed == base and untrimmed > trimmed else 'FAIL'}")
    ok &= trimmed == base and untrimmed > trimmed

    # And the star null a BLUR leaves behind, which is the number the pass band has to be anchored
    # on. It is well under 1, which is the whole point: those stars are gone before the model is
    # handed anything, so a floor near 1.0 asks it to invent them.
    #
    # Both plates carry the SAME noise and the threshold comes from the truth, mirroring the gate
    # (`detect(self.li[i], *self.truth_stats[i])`). Measuring a noisy blurred plate against a
    # NOISELESS plate's MAD instead reads 19.75: a threshold built on a near-zero MAD detects the
    # whole frame. The units have to match on both sides of a ratio, and here that means the noise.
    # Two things the width tests above do not need and this demo cannot do without: a RANGE of star
    # brightnesses, so some sit near the detection threshold, and a blur that CONSERVES FLUX, so a
    # widened star loses peak. `plate` draws every star at peak 1.0 regardless of width, which is a
    # fine model of a star and a useless model of blurring: redrawn wider at the same peak, not one
    # star ever crosses the threshold and the null reads exactly 1.000 while the caption claims it
    # should not. Same position draw on both sides (`seed=`), so only the width and peak differ.
    amps = np.geomspace(1.0, 0.15, 24)
    scale = (3.0 / 4.5) ** 2
    truth_n = plate(3.0, noise=0.02, amps=amps, seed=7)
    mt = float(np.median(truth_n))
    _, madt = M.bg_stats(truth_n)
    n_truth = len(detect(truth_n, mt, madt)[0])
    n_blur = len(detect(plate(4.5, noise=0.02, amps=amps * scale, seed=7), mt, madt)[0])
    ratio = n_blur / max(1, n_truth)
    print(f"  stars surviving a flux-conserving blur: {n_blur}/{n_truth} = {ratio:.3f}  "
          f"{'ok' if ratio < 1.0 else 'FAIL (the demo is not demonstrating anything)'}")
    ok &= ratio < 1.0

    print("self-test PASSED" if ok else "self-test FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    import sys
    if "--self-test" in sys.argv:
        raise SystemExit(_self_test())
    print(__doc__)
