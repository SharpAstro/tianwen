"""--oversample-sessions / --oversample-factor in n2n_smoke.py (#1947, docs/plans/star-remover-training.md, "The SMC's wing
check"): off, the run is the run it always was; on, the listed sessions' cells are drawn that many times as often.

    cd training/denoise && python -m unittest test_n2n_oversample -v

numpy only.
"""
import argparse
import os
import tempfile
import unittest

import numpy as np

import n2n_smoke as S

KEYS = [["a", 0, 0], ["a", 1, 0], ["b", 0, 0], ["b", 1, 0], ["b", 2, 0], ["c", 0, 0], ["v", 0, 0]]
META = {"cells": 7, "train_cells": 6, "keys": KEYS}


def args(**over):
    """The namespace resume_fingerprint reads, the recipe's switches at their defaults, over which `over` is laid."""
    base = dict(synthetic=True, synthetic_target="master", loss="l2", channel_noise=False, upsample=False, cond_map=True,
                band_loss=0.0, band_scales="1,2", base=32, seed=3, batch=4, lr=1e-3, schedule="plateau", patience=3,
                max_decays=4, min_improve=0.01, val_every=200, val_loss_cells=240, loss_mask=False, star_loss=None,
                half_pairs=False, pair_time="any", snapshot_at=0, steps=100)
    base.update(over)
    return argparse.Namespace(**base)


def session_file(*ids, comment=True):
    fd, path = tempfile.mkstemp(suffix=".txt")
    with os.fdopen(fd, "w", encoding="utf-8") as fh:
        if comment:
            fh.write("# the heavy-winged nights\n")
        for i in ids:
            fh.write(f"{i}  # a note\n")
    return path


class OversampleTests(unittest.TestCase):
    def test_off_leaves_the_fingerprint_as_it_was(self):
        before = S.resume_fingerprint(args(), META)
        self.assertEqual(S.resume_fingerprint(args(oversample_factor=1.0, oversample_sessions=None), META), before)
        self.assertNotIn("oversample_factor", before)

    def test_on_records_the_sessions_and_the_factor_and_nothing_else_moves(self):
        path = session_file("b", "a")
        before = S.resume_fingerprint(args(), META)
        on = S.resume_fingerprint(args(oversample_factor=4.0, oversample_sessions=path), META)
        self.assertEqual(on["oversample_sessions"], ["a", "b"])
        self.assertEqual(on["oversample_factor"], 4.0)
        self.assertEqual({k: v for k, v in on.items() if not k.startswith("oversample_")}, before)

    def test_a_listed_cell_is_drawn_factor_times_as_often(self):
        p = S.cell_draw_weights(KEYS, 6, ["b"], 4.0)
        self.assertEqual(len(p), 6, "only the training cells are drawn")
        self.assertAlmostEqual(p.sum(), 1.0)
        self.assertAlmostEqual(p[2] / p[0], 4.0)
        self.assertAlmostEqual(p[2], p[4])
        draws = np.random.default_rng(1).choice(6, 200_000, p=p)
        share = np.isin(draws, [2, 3, 4]).mean()
        self.assertAlmostEqual(share, 12.0 / 15.0, delta=0.005)

    def test_a_session_outside_the_training_cells_is_refused(self):
        with self.assertRaises(ValueError):
            S.cell_draw_weights(KEYS, 6, ["v"], 4.0)
        with self.assertRaises(ValueError):
            S.cell_draw_weights(KEYS, 6, ["typo"], 4.0)
        with self.assertRaises(ValueError):
            S.cell_draw_weights(KEYS, 6, ["a"], 0.0)

    def test_the_list_reads_ids_past_comments(self):
        self.assertEqual(S.read_session_list(session_file("x|y|z", "w")), ["x|y|z", "w"])

    def test_the_option_is_on_the_command_line_and_off_by_default(self):
        import subprocess
        import sys
        out = subprocess.run([sys.executable, S.__file__, "--help"], capture_output=True, text=True, check=True).stdout
        self.assertIn("--oversample-sessions", out)
        self.assertIn("--oversample-factor", out)


if __name__ == "__main__":
    unittest.main()
