"""--speckle-weight in n2n_smoke.py (#1362, docs/plans/star-remover-training.md, "The speckle teacher"): off, the run is the
run it always was; on, the term is added to the objective.

    cd training/denoise && python -m unittest test_n2n_speckle_option -v

The fingerprint cases need numpy only. The rest need torch (CPU is enough) and are skipped without it, as test_n2n_speckle's.
They run on a tiny tile and a one-layer net, no cache: `add_speckle` is the ONE place the term joins the objective (the
training step and the plateau's held-out objective both call it), so what it does off and on is what the trainer does.
"""
import argparse
import unittest

import numpy as np

import n2n_smoke as S

try:
    import torch

    import n2n_speckle as SP
except ImportError:  # pragma: no cover - a machine with no torch wheel (win-arm64 on Python 3.13+)
    torch = None

META = {"cells": 10, "train_cells": 8}


def args(**over):
    """The namespace resume_fingerprint reads, the recipe's switches at their defaults, over which `over` is laid."""
    base = dict(synthetic=True, synthetic_target="master", loss="l2", channel_noise=False, upsample=False, cond_map=True,
                band_loss=0.0, band_scales="1,2", base=32, seed=3, batch=4, lr=1e-3, schedule="plateau", patience=3,
                max_decays=4, min_improve=0.01, val_every=200, val_loss_cells=240, loss_mask=False, star_loss=None,
                half_pairs=False, pair_time="any", snapshot_at=0, steps=100)
    base.update(over)
    return argparse.Namespace(**base)


class FingerprintTests(unittest.TestCase):
    def test_off_leaves_the_fingerprint_as_it_was(self):
        before = S.resume_fingerprint(args(), META)
        for off in (dict(speckle_weight=0.0), dict(speckle_weight=0.0, speckle_sigma=7.0)):
            self.assertEqual(S.resume_fingerprint(args(**off), META), before)
        self.assertNotIn("speckle_weight", before)
        self.assertNotIn("speckle_sigma", before)

    def test_on_records_the_weight_and_the_floor_and_nothing_else_moves(self):
        before = S.resume_fingerprint(args(), META)
        on = S.resume_fingerprint(args(speckle_weight=2.0, speckle_sigma=50.0), META)
        self.assertEqual(on["speckle_weight"], 2.0)
        self.assertEqual(on["speckle_sigma"], 50.0)
        self.assertEqual({k: v for k, v in on.items() if not k.startswith("speckle_")}, before)

    def test_another_weight_or_floor_is_another_run(self):
        a = S.resume_fingerprint(args(speckle_weight=1.0, speckle_sigma=100.0), META)
        self.assertNotEqual(a, S.resume_fingerprint(args(speckle_weight=2.0, speckle_sigma=100.0), META))
        self.assertNotEqual(a, S.resume_fingerprint(args(speckle_weight=1.0, speckle_sigma=50.0), META))

    def test_the_option_is_on_the_command_line_and_off_by_default(self):
        import subprocess
        import sys
        out = subprocess.run([sys.executable, S.__file__, "--help"], capture_output=True, text=True, check=True).stdout
        self.assertIn("--speckle-weight", out)
        self.assertIn("--speckle-sigma", out)
        self.assertEqual(S.resume_fingerprint(args(), META), S.resume_fingerprint(args(speckle_weight=0.0), META))


SIDE = 64
STAR_X, STAR_Y = 30, 33
NOISE = 0.01


@unittest.skipIf(torch is None, "torch is not installed")
class ObjectiveTests(unittest.TestCase):
    """A 2-tile, 3-channel batch: truth y a noisy flat sky, the draw x = y + a star at (STAR_X, STAR_Y) (200 sigma), the
    plane the draw's noise as stored. The 'net' is a 1x1 conv fixed to the negative identity, so its output has the star
    DUG (the sky's negative, the star far below it): a speckle at the site, whatever the seed."""

    def setUp(self):
        g = torch.Generator().manual_seed(11)
        self.y = 0.2 + NOISE * torch.randn(2, 3, SIDE, SIDE, generator=g)
        yy, xx = torch.meshgrid(torch.arange(SIDE), torch.arange(SIDE), indexing="ij")
        star = 2.0 * torch.exp(-((xx - STAR_X) ** 2 + (yy - STAR_Y) ** 2) / (2 * 1.5**2))
        self.x = self.y + star
        self.plane = np.full((2, SIDE, SIDE), NOISE * S.PLANE_SCALE, dtype=np.float32)
        self.fresh()

    def fresh(self):
        torch.manual_seed(5)
        self.model = torch.nn.Conv2d(3, 3, 1)
        with torch.no_grad():
            self.model.weight.copy_(-torch.eye(3).reshape(3, 3, 1, 1))
            self.model.bias.zero_()
        return self.model

    def step(self, a, lr=0.1):
        """One SGD step the way train() takes it: the pixel term, then add_speckle, backward, step. Returns (loss, weights)."""
        model = self.fresh()
        opt = torch.optim.SGD(model.parameters(), lr=lr)
        pred = model(self.x)
        loss = torch.nn.functional.mse_loss(pred, self.y)
        loss, term = S.add_speckle(loss, a, pred, self.x, self.y, self.plane)
        opt.zero_grad(set_to_none=True)
        loss.backward()
        opt.step()
        return loss, term, [p.detach().clone() for p in model.parameters()]

    def on(self, weight=1.0, floor=20.0):
        return args(speckle_weight=weight, speckle_sigma=floor)

    def test_the_star_is_a_site(self):
        layer = S.star_layer_sigmas(self.x, self.y, torch.from_numpy(self.plane))
        sites = SP.peak_sites(layer, 20.0, S.BORDER + 3)
        got = {(int(t), int(x), int(y)) for t, x, y in sites.tolist()}
        self.assertEqual(got, {(0, STAR_X, STAR_Y), (1, STAR_X, STAR_Y)})
        self.assertEqual(SP.peak_sites(layer, 1e6, S.BORDER + 3).shape, (0, 3), "under the floor, no site")
        self.assertEqual(SP.peak_sites(layer, 20.0, 40).shape, (0, 3), "a site too near the rim is not read")

    def test_off_is_the_loss_object_itself_and_the_same_step_to_the_bit(self):
        a = args(speckle_weight=0.0)
        self.fresh()
        pred = self.model(self.x)
        loss = torch.nn.functional.mse_loss(pred, self.y)
        same, term = S.add_speckle(loss, a, pred, self.x, self.y, self.plane)
        self.assertIs(same, loss)
        self.assertIsNone(term)
        # The step the trainer took before the option: pixel term only.
        model = self.fresh()
        opt = torch.optim.SGD(model.parameters(), lr=0.1)
        before = torch.nn.functional.mse_loss(model(self.x), self.y)
        opt.zero_grad(set_to_none=True)
        before.backward()
        opt.step()
        want = [p.detach().clone() for p in model.parameters()]
        loss_off, _, got = self.step(a)
        self.assertEqual(float(loss_off), float(before))
        for g, w in zip(got, want):
            self.assertTrue(torch.equal(g, w), "a step with the option off moves the weights as it always did")

    def test_on_adds_the_term_to_the_loss(self):
        loss_off, _, _ = self.step(args(speckle_weight=0.0))
        for weight in (1.0, 250.0):
            loss_on, term, _ = self.step(self.on(weight))
            self.assertGreater(float(term), 0.0, "the dug core is a speckle")
            self.assertAlmostEqual(float(loss_on - loss_off), weight * float(term), delta=1e-6 * weight * float(term))

    def test_the_term_is_the_penalty_at_the_sites_in_the_pixel_terms_units(self):
        self.fresh()
        pred = self.model(self.x)
        term = S.speckle_term(pred, self.x, self.y, torch.from_numpy(self.plane), 20.0)
        sites = torch.tensor([[0, STAR_X, STAR_Y], [1, STAR_X, STAR_Y]], dtype=torch.float64)
        cap = SP.noise_cap(self.x.mean(1))
        raw = SP.speckle_penalty(pred.mean(1), sites, cap)
        area = 2 * (SIDE - 2 * S.BORDER) ** 2
        self.assertAlmostEqual(float(term), float(raw) / area, delta=1e-6 * float(term))

    def test_on_changes_the_gradient_and_descending_it_lifts_the_dug_core(self):
        _, _, off = self.step(args(speckle_weight=0.0))
        _, term, on = self.step(self.on(10.0))
        self.assertFalse(all(torch.equal(a, b) for a, b in zip(off, on)), "the term reaches the weights")
        # The same net after the on-step reads less penalty than before it (the term's own descent).
        model = self.fresh()
        pred = model(self.x)
        start = float(S.speckle_term(pred, self.x, self.y, torch.from_numpy(self.plane), 20.0))
        with torch.no_grad():
            for p, q in zip(model.parameters(), on):
                p.copy_(q)
        after = float(S.speckle_term(model(self.x), self.x, self.y, torch.from_numpy(self.plane), 20.0))
        self.assertLess(after, start)

    def test_a_batch_with_no_site_adds_nothing(self):
        self.fresh()
        pred = self.model(self.y)
        flat = self.y
        loss = torch.nn.functional.mse_loss(pred, self.y)
        got, term = S.add_speckle(loss, self.on(5.0), pred, flat, self.y, self.plane)
        self.assertEqual(float(term.detach()), 0.0)
        self.assertEqual(float(got.detach()), float(loss.detach()))
        got.backward()

    def test_a_clean_output_has_no_penalty(self):
        # The truth as the output: nothing dug, nothing falls short of the sky at the sites.
        term = S.speckle_term(self.y.clone().requires_grad_(True), self.x, self.y, torch.from_numpy(self.plane), 20.0)
        self.assertEqual(float(term), 0.0)


if __name__ == "__main__":
    unittest.main()
