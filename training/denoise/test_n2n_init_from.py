"""--init-from in n2n_smoke.py (docs/plans/star-remover-training.md, "R3w"): off, the run and its resume fingerprint are what
they always were; on, the model begins at another run's saved weights, a file of another shape is refused, and the file's
name is part of the fingerprint.

    cd training/denoise && python -m unittest test_n2n_init_from -v
"""
import argparse
import unittest

import torch

import n2n_smoke as S

META = {"cells": 7, "train_cells": 6, "keys": [["a", 0, 0]]}


def args(**over):
    """The namespace resume_fingerprint and init_weights read, the recipe's switches at their defaults."""
    base = dict(synthetic=True, synthetic_target="master", loss="l2", channel_noise=False, upsample=True, cond_map=True,
                band_loss=0.0, band_scales="1,2", base=8, seed=3, batch=4, lr=1e-3, schedule="plateau", patience=3,
                max_decays=4, min_improve=0.01, val_every=200, val_loss_cells=240, loss_mask=False, star_loss=None,
                half_pairs=False, pair_time="any", snapshot_at=0, steps=100, init_from=None)
    base.update(over)
    return argparse.Namespace(**base)


def saved(model, **over):
    out = {"model": {k: v.clone() for k, v in model.state_dict().items()}, "base": 8, "upsample": True}
    out.update(over)
    return out


class InitFrom(unittest.TestCase):
    def test_off_leaves_the_fingerprint_as_it_was(self):
        without = argparse.Namespace(**{k: v for k, v in vars(args()).items() if k != "init_from"})
        self.assertEqual(S.resume_fingerprint(args(), META), S.resume_fingerprint(without, META))
        self.assertNotIn("init_from", S.resume_fingerprint(args(), META))

    def test_on_puts_the_files_name_in_the_fingerprint(self):
        fp = S.resume_fingerprint(args(init_from="D:/somewhere/r3v_synth22_s0.pt"), META)
        self.assertEqual(fp["init_from"], "r3v_synth22_s0.pt")
        moved = S.resume_fingerprint(args(init_from="/mnt/cache/r3v_synth22_s0.pt"), META)
        self.assertEqual(fp, moved, "a run moved with its weights is the same run")
        self.assertNotEqual(fp, S.resume_fingerprint(args(init_from="r3v_synth22_s1.pt"), META))

    def test_the_model_begins_at_the_saved_weights(self):
        torch.manual_seed(1)
        source = torch.nn.Sequential(torch.nn.Conv2d(3, 8, 3), torch.nn.Conv2d(8, 3, 3))
        torch.manual_seed(2)
        model = torch.nn.Sequential(torch.nn.Conv2d(3, 8, 3), torch.nn.Conv2d(8, 3, 3))
        self.assertFalse(torch.equal(model[0].weight, source[0].weight))
        S.init_weights(model, saved(source), args(init_from="a.pt"))
        for a, b in zip(model.state_dict().values(), source.state_dict().values()):
            self.assertTrue(torch.equal(a, b))

    def test_a_file_of_another_shape_is_refused(self):
        source = torch.nn.Sequential(torch.nn.Conv2d(3, 8, 3), torch.nn.Conv2d(8, 3, 3))
        wider = torch.nn.Sequential(torch.nn.Conv2d(3, 16, 3), torch.nn.Conv2d(16, 3, 3))
        with self.assertRaises(SystemExit) as fit:
            S.init_weights(wider, saved(source), args(init_from="a.pt"))
        self.assertIn("does not fit", str(fit.exception))
        self.assertIn("size mismatch", str(fit.exception), "the cause is named, not only the header")
        with self.assertRaises(SystemExit) as base:
            S.init_weights(source, saved(source, base=32), args(init_from="a.pt"))
        self.assertIn("base 32", str(base.exception))
        with self.assertRaises(SystemExit) as empty:
            S.init_weights(source, {"base": 8}, args(init_from="a.pt"))
        self.assertIn("no model state", str(empty.exception))

    def test_a_runs_own_resume_checkpoint_wins_over_init_from(self):
        import os, tempfile
        with tempfile.TemporaryDirectory() as d:
            present = os.path.join(d, "ck.pt")
            open(present, "wb").close()
            absent = os.path.join(d, "none.pt")
            for resume in (False, True):
                for path, exists in ((present, True), (absent, False)):
                    want = not (resume and exists)
                    self.assertEqual(S.should_init_from(args(init_from="a.pt", resume=resume), path), want)
                    self.assertFalse(S.should_init_from(args(init_from=None, resume=resume), path))


if __name__ == "__main__":
    unittest.main()
