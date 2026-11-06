"""--flux-loss in n2n_smoke.py (R3o, docs/plans/star-remover-training.md): each star's footprint is found in the star layer,
and the term is the mean over footprints of (flux left in or dug / the star's flux) squared.

    cd training/denoise && python -m unittest test_n2n_flux -v

Needs torch (CPU is enough) and scipy, skipped without torch. The tiles are built so every footprint and every ratio is known.
"""
import unittest

import numpy as np

import n2n_smoke as S

try:
    import torch
except ImportError:  # pragma: no cover - a machine with no torch wheel
    torch = None

B = S.BORDER
SIZE = 2 * B + 32


def star(cy, cx, peak, size=SIZE, width=1.5):
    yy, xx = np.mgrid[0:size, 0:size]
    return peak * np.exp(-((yy - cy) ** 2 + (xx - cx) ** 2) / (2 * width * width))


def plane(sigma, batch):
    """A noise plane stating `sigma` everywhere, in the PLANE_SCALE units the trainer stores."""
    return torch.full((batch, SIZE, SIZE), sigma * S.PLANE_SCALE)


@unittest.skipIf(torch is None, "needs torch")
class FluxTermTests(unittest.TestCase):
    def setUp(self):
        self.truth = torch.full((2, 3, SIZE, SIZE), 0.1)
        a = star(B + 8, B + 8, 1.0)
        b = star(B + 22, B + 22, 0.4)
        faint = star(B + 8, B + 24, 0.002)          # 2 sigma at its peak: under the 5 sigma floor
        layer = np.stack([a + b + faint, star(B + 16, B + 16, 0.6), np.zeros((SIZE, SIZE))])
        self.layers = [torch.from_numpy(np.stack([layer[0]] * 3)).float(),
                       torch.from_numpy(np.stack([layer[1]] * 3)).float()]
        self.x = self.truth + torch.stack(self.layers)
        self.plane = plane(0.001, 2)

    def test_footprints_are_the_stars_over_the_floor_numbered_across_the_batch(self):
        labels, count = S.flux_footprints(self.x, self.truth, self.plane, 5.0, 2, B)
        self.assertEqual(count, 3, "two stars in tile 0 (the faint one under the floor), one in tile 1")
        self.assertEqual(tuple(labels.shape), (2, SIZE - 2 * B, SIZE - 2 * B))
        self.assertEqual(sorted(set(labels[0].unique().tolist()) - {0}), [1, 2])
        self.assertEqual(labels[1].unique().tolist(), [0, 3])
        self.assertEqual(int(labels[0, 8, 24 - 0]), 0, "the 2 sigma star is no footprint")

    def test_the_term_is_the_mean_squared_share_of_each_stars_flux_left_or_dug(self):
        labels, count = S.flux_footprints(self.x, self.truth, self.plane, 5.0, 2, B)
        # Output: star a half left in, star b 20 % dug past its truth, tile 1's star removed exactly.
        lab0 = labels[0].numpy()
        a_id = int(lab0[8, 8])
        a_mask = np.zeros((SIZE, SIZE), bool)
        a_mask[B:-B, B:-B] = lab0 == a_id
        b_mask = np.zeros((SIZE, SIZE), bool)
        b_mask[B:-B, B:-B] = (lab0 > 0) & (lab0 != a_id)
        s0 = self.layers[0][0].numpy()
        left = np.where(a_mask, 0.5 * s0, 0.0) + np.where(b_mask, -0.2 * s0, 0.0)
        pred = self.truth.clone()
        pred[0] += torch.from_numpy(left).float()
        pred.requires_grad_(True)
        term, kept = S.flux_term(pred, self.x, self.truth, labels, count, B)
        self.assertEqual(kept, 3)
        self.assertAlmostEqual(float(term.detach()), (0.5 ** 2 + 0.2 ** 2 + 0.0) / 3, places=5)
        term.backward()
        self.assertGreater(float(pred.grad.abs().sum()), 0.0, "the term trains the output")
        self.assertEqual(float(pred.grad[1].abs().sum()), 0.0, "an exactly removed star asks for nothing")

    def test_a_tile_with_no_star_gives_no_footprint(self):
        labels, count = S.flux_footprints(self.truth, self.truth, self.plane, 5.0, 2, B)
        self.assertEqual(count, 0)
        self.assertEqual(int(labels.abs().sum()), 0)


class FluxFingerprintTests(unittest.TestCase):
    def test_off_leaves_the_fingerprint_as_it_was_and_on_records_the_recipe(self):
        import argparse
        base = dict(synthetic=True, synthetic_target="master", loss="l2", channel_noise=False, upsample=False,
                    cond_map=True, band_loss=0.0, band_scales="1,2", base=32, seed=3, batch=4, lr=1e-3,
                    schedule="plateau", patience=3, max_decays=4, min_improve=0.0, val_every=500, val_loss_cells=8,
                    loss_mask=False, star_loss=None, half_pairs=False, pair_time="any", snapshot_at=0, steps=1000)
        meta = {"cells": 10, "train_cells": 8}
        off = S.resume_fingerprint(argparse.Namespace(**base), meta)
        on = S.resume_fingerprint(argparse.Namespace(**base, flux_loss="auto", flux_sigma=5.0, flux_grow=2), meta)
        self.assertNotIn("flux_loss", off)
        self.assertEqual((on["flux_loss"], on["flux_sigma"], on["flux_grow"]), ("auto", 5.0, 2))


if __name__ == "__main__":
    unittest.main()
