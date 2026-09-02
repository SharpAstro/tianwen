"""Selection metrics cheap enough to run DURING training, so a run can be stopped on them.

Loss cannot select a denoiser here and neither can PSNR: both fall fastest for a model that
irons the frame flat, because the background is most of the pixels. The two measures that
actually reversed verdicts in the smoke runs were the fabrication count and the residual
correlation, and both only ran post-hoc, after the GPU time was already spent.

This makes them a mid-training probe. The expensive half (detecting the master's stars, building
the star-free masks) is precomputed ONCE per Gate; each evaluation is then one forward pass over
a small slice, so a probe every few hundred steps costs seconds on an eleven-minute run.

Reuses n2n_metrics' estimators rather than restating them, so a mid-training number and the
final report cannot disagree about what they measure.
"""
import numpy as np
import torch
from scipy.ndimage import binary_dilation, maximum_filter

import n2n_metrics as M
import n2n_smoke as S

# The bucket every variant differed in. A gate on the bright end would report success for all.
FAINT_BUCKET = 0
DOT_SIGMA = 5.0


def _whole_mad(t):
    """Whole-tile median and MAD. Deliberately NOT the darkest-half estimator: as a DETECTION
    bar that one is far too low and compresses every model into the same 18-25% real."""
    m = np.median(t)
    return m, float(np.median(np.abs(t - m))) + 1e-12


class Gate:
    """Mid-training probe over a fixed held-out slice.

    cells: absolute cache indices to probe. Keep it small (~24); this runs inside the training
    loop and its job is to rank checkpoints of ONE run against each other, not to be the report.
    """

    def __init__(self, mm, cells, device):
        self.dev = device
        self.masters = np.asarray(mm[cells, S.SLOT_MASTER], dtype=np.float32)
        self.subs = np.asarray(mm[cells, 1], dtype=np.float32)
        m_c = S.crop(self.masters)
        self.lm = m_c.mean(axis=1)
        self.stars = M.star_table(self.lm)

        # Star-free mask: what is left is nebulosity and sky, where a structure loss is judged.
        masks = []
        for t in self.lm:
            med, mad = np.median(t), M.bg_stats(t)[1]
            det = (t >= maximum_filter(t, size=5)) & (t > med + 5 * mad)
            masks.append(~binary_dilation(det, np.ones((9, 9), bool)))
        self.masks = np.array(masks)

        # Truth for the fabrication count, dilated 3x3 for centroid wobble.
        truth = []
        for t in self.lm:
            med, mad = _whole_mad(t)
            d = (t >= maximum_filter(t, size=5)) & (t > med + 8 * mad)
            truth.append(binary_dilation(d, np.ones((3, 3), bool)))
        self.truth = np.array(truth)

        self.base_noise = float(np.mean([M.bg_stats(t)[1] for t in self.lm]))
        # The raw sub's own spurious count is the honest floor for the fabrication gate. It is
        # not zero, and comparing against zero makes every model look like it invents signal.
        # Kept PER TILE as well, because the log-ratio form below is a per-tile statistic and
        # cannot be rebuilt from the mean.
        self.floor_per_tile = self.spurious_per_tile(S.crop(self.subs))
        self.floor_spurious = float(self.floor_per_tile.mean())
        # The input's own per-tile noise scale, frozen for the absolute-bar count. The floor needs
        # no separate absolute version: for the raw sub the two bars ARE the same number, which is
        # what makes this floor legitimate to subtract from an absolute-bar output count.
        self.sub_mad = np.array([_whole_mad(t)[1]
                                 for t in S.crop(self.subs).mean(axis=1)])

    def _forward(self, model, cond, src):
        """cond is the conditioning PLANE COUNT (falsy when off), not a flag, so the probe builds
        the same input the training step does instead of assuming a single scalar plane."""
        out = []
        with torch.no_grad():
            for i in range(0, len(src), 8):
                x = torch.from_numpy(src[i:i + 8]).to(self.dev)
                out.append(model(S.with_sigma(x, planes=cond) if cond else x).cpu().numpy())
        return np.concatenate(out)

    def spurious_per_tile(self, arr, ref_mad=None):
        """Point sources PER TILE that sit on no master star, as an array rather than a mean.

        The per-tile form exists because the aggregate does not transfer between sessions, so
        candidate re-normalisations (ratio, paired median, sign fraction) need the distribution
        and not just its first moment. `_spurious` is its mean, so the gate and any normalisation
        study can never disagree about what was counted.

        `ref_mad` fixes the detection bar in ABSOLUTE units instead of re-deriving it from the
        array being counted, and it exists because the default is not measuring what it reads as.
        The bar is `med + 5 * MAD`, so a model that crushes the background lowers its OWN bar and
        counts speckles the input already carried at ~4 MAD as 7-MAD detections: same photons,
        lower bar. That is most of the apparent fabrication, and it scales with denoising strength,
        which is exactly the axis the counts were being compared along. Passing the INPUT's per-tile
        MAD holds one physical threshold across input and output, so the floor subtraction is
        finally coherent -- and the raw sub's own count is unchanged by it, since for the input the
        two bars are the same number."""
        la = arr.mean(axis=1)
        out = np.empty(len(la), dtype=np.float64)
        for i in range(len(la)):
            med, mad = _whole_mad(la[i])
            if ref_mad is not None:
                mad = ref_mad[i]
            d = (la[i] >= maximum_filter(la[i], size=5)) & (la[i] > med + DOT_SIGMA * mad)
            ys, xs = np.nonzero(d)
            out[i] = len(ys) - int(self.truth[i][ys, xs].sum())
        return out

    def _spurious(self, arr):
        """Mean point sources per tile that sit on NO master star."""
        return float(self.spurious_per_tile(arr).mean())

    def evaluate(self, model, cond):
        """One probe. Returns the numbers a run should be selected on, never the loss."""
        was_training = model.training
        model.eval()
        try:
            # Applied to the MASTER: the deployment case, and where noise/amp/detect mean what
            # the final report means by them.
            den_m = S.crop(self._forward(model, cond, self.masters))
            la = den_m.mean(axis=1)
            noise = float(np.mean([M.bg_stats(t)[1] for t in la])) / self.base_noise
            amp, det, _ = M.measure(la, self.stars, self.lm)

            resid = []
            for i in range(len(la)):
                d = (self.lm[i] - la[i])[self.masks[i]]
                o = la[i][self.masks[i]]
                if d.std() > 0 and o.std() > 0:
                    resid.append(float(np.corrcoef(d, o)[0, 1]))

            # Applied to a SUB: where the model extrapolates hardest, so invention shows up.
            # The gate's direction depends on the input, which is why it is measured here and
            # not on the master, where the input IS the reference and low would mean erasure.
            den_s = S.crop(self._forward(model, cond, self.subs))
            spur_per_tile = self.spurious_per_tile(den_s)
            spur_abs_per_tile = self.spurious_per_tile(den_s, ref_mad=self.sub_mad)
        finally:
            if was_training:
                model.train()

        # Two readings of the same counts, because they answer different questions. The DIFFERENCE
        # of means is what the gate thresholds on, and it does not survive a change of session
        # (delta 8.09 against a 4.88 across-model spread). The per-tile LOG RATIO does preserve the
        # ORDERING across sessions (rank rho +0.86 against +0.54 for the difference), which is the
        # only property a stopping rule needs, so it is reported here to make a relative rule
        # testable. +1 on both sides keeps a zero-count tile finite.
        return {
            "noise": noise,
            "faint_amp": amp[FAINT_BUCKET],
            "faint_detect": det[FAINT_BUCKET],
            "resid_corr": float(np.mean(resid)) if resid else float("nan"),
            "spurious": float(spur_per_tile.mean()),
            "spurious_over_floor": float(spur_per_tile.mean()) - self.floor_spurious,
            "spurious_abs": float(spur_abs_per_tile.mean()),
            "spurious_abs_over_floor": float(spur_abs_per_tile.mean()) - self.floor_spurious,
            "log_ratio": float(np.mean(np.log((spur_per_tile + 1.0)
                                              / (self.floor_per_tile + 1.0)))),
        }

    @staticmethod
    def header():
        return (f"{'noise':>7} {'f-amp':>6} {'f-det':>6} {'resid':>7} "
                f"{'spur':>6} {'vs floor':>9} {'absbar':>7} {'abs vs fl':>10} {'logratio':>9}")

    @staticmethod
    def format(m):
        return (f"{m['noise']:6.2f}x {m['faint_amp']:6.2f} {m['faint_detect']:6.2f} "
                f"{m['resid_corr']:+7.3f} {m['spurious']:6.1f} {m['spurious_over_floor']:+9.1f} "
                f"{m['spurious_abs']:7.1f} {m['spurious_abs_over_floor']:+10.1f} "
                f"{m['log_ratio']:+9.4f}")
