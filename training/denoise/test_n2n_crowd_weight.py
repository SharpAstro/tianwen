"""--crowd-weight in n2n_smoke.py (docs/plans/star-remover-training.md, "The routes not yet tried"): the count of the star
layer's peaks about each pixel, ramped from weight 1 to the whole weight.

    cd training/denoise && python -m unittest test_n2n_crowd_weight -v

Needs torch (CPU is enough), skipped without it. The tiles are built so every count is known.
"""
import unittest

import numpy as np

import n2n_smoke as S

try:
    import torch
except ImportError:  # pragma: no cover - a machine with no torch wheel
    torch = None

SIZE = 96
SIGMA = 0.001


def star(cy, cx, peak, width=1.0):
    yy, xx = np.mgrid[0:SIZE, 0:SIZE]
    return peak * np.exp(-((yy - cy) ** 2 + (xx - cx) ** 2) / (2 * width * width))


def tile(layer):
    """(x, y, plane) for one tile whose star layer is `layer`: a flat truth, three equal channels, noise SIGMA."""
    truth = torch.full((1, 3, SIZE, SIZE), 0.1)
    x = truth + torch.from_numpy(np.stack([layer] * 3)).float().unsqueeze(0)
    return x, truth, torch.full((1, SIZE, SIZE), SIGMA * S.PLANE_SCALE)


@unittest.skipIf(torch is None, "needs torch")
class CrowdWeightTests(unittest.TestCase):
    def test_a_lone_star_keeps_the_plain_objective(self):
        x, y, plane = tile(star(48, 48, 50 * SIGMA))
        w = S.crowd_weights(x, y, plane, 3.0, 8, 2.0, 6.0, 4.0)
        self.assertTrue(torch.all(w == 1.0), "one peak is under the ramp's start everywhere")

    def test_a_crowded_patch_takes_the_whole_weight_and_the_sparse_sky_none(self):
        # 25 stars on a 5 x 5 grid 4 px apart about (24, 24), one lone star far off at (80, 80).
        layer = sum(star(16 + 4 * i, 16 + 4 * j, 20 * SIGMA) for i in range(5) for j in range(5))
        layer = layer + star(80, 80, 20 * SIGMA)
        x, y, plane = tile(layer)
        w = S.crowd_weights(x, y, plane, 3.0, 8, 2.0, 20.0, 4.0)
        self.assertAlmostEqual(float(w[0, 0, 24, 24]), 4.0, places=5, msg="all 25 peaks inside the 17 px square: over full")
        self.assertAlmostEqual(float(w[0, 0, 80, 80]), 1.0, places=5, msg="one peak: under the start")
        self.assertEqual(tuple(w.shape), (1, 1, SIZE, SIZE))

    def test_the_ramp_is_linear_between_its_ends(self):
        # Exactly 11 peaks inside the square about (48, 48) of radius 10 (21 px): a row of 11 stars 2 px apart there.
        layer = sum(star(48, 38 + 2 * k, 20 * SIGMA, width=0.5) for k in range(11))
        x, y, plane = tile(layer)
        w = S.crowd_weights(x, y, plane, 3.0, 10, 1.0, 21.0, 3.0)
        self.assertAlmostEqual(float(w[0, 0, 48, 48]), 1.0 + 2.0 * (11 - 1) / 20, places=5)

    def test_peaks_under_the_floor_do_not_count(self):
        layer = sum(star(16 + 4 * i, 16 + 4 * j, 2 * SIGMA) for i in range(5) for j in range(5))
        x, y, plane = tile(layer)
        w = S.crowd_weights(x, y, plane, 3.0, 8, 2.0, 20.0, 4.0)
        self.assertTrue(torch.all(w == 1.0), "2 sigma peaks are under the 3 sigma floor")

    def test_off_leaves_the_fingerprint_as_it_was_and_on_records_the_recipe(self):
        import argparse
        base = dict(synthetic=True, synthetic_target="master", loss="l2", channel_noise=False, upsample=False,
                    cond_map=True, band_loss=0.0, band_scales="1,2", base=32, seed=3, batch=4, lr=1e-3,
                    schedule="plateau", patience=3, max_decays=4, min_improve=0.0, val_every=500, val_loss_cells=8,
                    loss_mask=False, star_loss=None, half_pairs=False, pair_time="any", snapshot_at=0, steps=1000)
        meta = {"cells": 10, "train_cells": 8}
        off = S.resume_fingerprint(argparse.Namespace(**base, crowd_weight=0.0), meta)
        on = S.resume_fingerprint(argparse.Namespace(**base, crowd_weight=3.0, crowd_sigma=3.0, crowd_radius=16,
                                                     crowd_from=10.0, crowd_full=30.0), meta)
        self.assertNotIn("crowd_weight", off, "off leaves a run as it was, so an older checkpoint still resumes")
        self.assertEqual((on["crowd_weight"], on["crowd_sigma"], on["crowd_radius"], on["crowd_from"], on["crowd_full"]),
                         (3.0, 3.0, 16, 10.0, 30.0))


if __name__ == "__main__":
    unittest.main()
