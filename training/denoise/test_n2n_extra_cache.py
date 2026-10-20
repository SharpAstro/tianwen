"""R2q's --extra-cache in n2n_smoke.py (docs/plans/star-remover-training.md, "Synthetic crowded clusters"): the target per
tile, the extra cache's all-ones loss mask, the split by whole scene, and the three-part held-out objective.

    cd training/denoise && python -m unittest test_n2n_extra_cache -v

numpy only: the batch and objective helpers are plain functions, so no torch or GPU is needed.
"""
import json
import os
import tempfile
import unittest

import numpy as np

import n2n_smoke as S


def fake_cache(cells, slots, draws, channels=3, side=4, seed=0):
    """A tile array [cells, slots, C, side, side] whose every value says where it came from (cell, slot, channel)."""
    rng = np.random.default_rng(seed)
    mm = rng.random((cells, slots, channels, side, side)).astype(np.float16)
    return mm


class TargetPerTile(unittest.TestCase):
    """A synth4 row reads what it always read (its draw, and the teacher's output on that draw); a cluster row reads its own
    draw and its own truth, slot 0."""

    def setUp(self):
        self.main = fake_cache(20, 11, 4, seed=1)
        self.teacher = fake_cache(20, 4, 4, seed=2)          # [cells, draws, C, H, W]
        self.extra = S.ExtraCache("clusters", fake_cache(9, 11, 1, seed=3), {"train_cells": 8, "draws": 1, "cells": 9})

    def target_of(self, i, slot):
        return self.teacher[i, slot - 1]

    def test_without_an_extra_cache_a_batch_is_the_one_the_trainer_always_drew(self):
        rng = np.random.default_rng(7)
        idx, a = rng.integers(0, 16, 8), rng.integers(1, 5, 8)
        x, y = S.synth_batch(self.main, idx, a, self.target_of)
        np.testing.assert_array_equal(x, np.ascontiguousarray(self.main[idx, a]))
        np.testing.assert_array_equal(y, np.ascontiguousarray(self.teacher[idx, a - 1]))

    def test_a_main_row_keeps_the_teachers_target_and_an_extra_row_its_own_truth(self):
        rng = np.random.default_rng(11)
        idx, a = rng.integers(0, 16, 64), rng.integers(1, 5, 64)
        pick = S.pick_extra(rng, 64, 0.4, self.extra.n_train, self.extra.draws)
        fe, ie, ae = pick
        self.assertTrue(fe.any() and (~fe).any())
        x, y = S.synth_batch(self.main, idx, a, self.target_of, self.extra, pick)
        for r in range(64):
            if fe[r]:
                np.testing.assert_array_equal(x[r], self.extra.mm[ie[r], ae[r]])
                np.testing.assert_array_equal(y[r], self.extra.mm[ie[r], 0], "a cluster tile regresses onto its truth")
                self.assertLess(ie[r], self.extra.n_train, "only a training cell of the extra cache is drawn")
            else:
                np.testing.assert_array_equal(x[r], self.main[idx[r], a[r]])
                np.testing.assert_array_equal(y[r], self.teacher[idx[r], a[r] - 1], "a synth4 tile keeps the teacher's target")

    def test_the_main_rows_are_drawn_as_without_the_extra_cache(self):
        # The extra pick is drawn after the main draws, so the main rows' cells and draws do not move.
        r1, r2 = np.random.default_rng(5), np.random.default_rng(5)
        idx1, a1 = r1.integers(0, 16, 32), r1.integers(1, 5, 32)
        idx2, a2 = r2.integers(0, 16, 32), r2.integers(1, 5, 32)
        S.pick_extra(r2, 32, 0.3, self.extra.n_train, self.extra.draws)
        np.testing.assert_array_equal(idx1, idx2)
        np.testing.assert_array_equal(a1, a2)

    def test_the_clean_target_without_a_teacher(self):
        rng = np.random.default_rng(3)
        idx, a = rng.integers(0, 16, 16), rng.integers(1, 5, 16)
        pick = S.pick_extra(rng, 16, 0.5, self.extra.n_train, self.extra.draws)
        fe = pick[0]
        _, y = S.synth_batch(self.main, idx, a, lambda i, s: self.main[i, S.SLOT_MASTER], self.extra, pick)
        np.testing.assert_array_equal(y[~fe], self.main[idx[~fe], S.SLOT_MASTER])


class AllOnesMask(unittest.TestCase):
    """The cluster export writes an all-ones .keep.f16 beside each draw; packed by --prepare-keep it stays all ones, and a
    batch's extra rows read it."""

    def test_prepare_keep_packs_the_cluster_masks_as_ones(self):
        with tempfile.TemporaryDirectory() as root, tempfile.TemporaryDirectory() as cache:
            tiles = os.path.join(root, "tiles", "s")
            os.makedirs(tiles)
            rows = []
            for frame in ("master", "deg000"):
                rel = f"tiles/s/x0_y0_{frame}.f16"
                rows.append({"Tile": rel, "SessionId": "synthetic-cluster|m|s0001|measured", "Frame": frame, "CellX": 0, "CellY": 0})
            ones = np.ones((S.TILE, S.TILE), dtype="<f2")
            ones.tofile(os.path.join(root, S.keep_path_for("tiles/s/x0_y0_deg000.f16")))
            with open(os.path.join(root, "tiles-manifest.jsonl"), "w", encoding="utf-8") as fh:
                fh.write("".join(json.dumps(r) + "\n" for r in rows))
            with open(os.path.join(cache, "meta.json"), "w", encoding="utf-8") as fh:
                json.dump({"cells": 1, "draws": 1, "keys": [["synthetic-cluster|m|s0001|measured", 0, 0]]}, fh)

            class Args:
                pass
            args = Args()
            args.root, args.manifest, args.cache = root, "tiles-manifest.jsonl", cache
            S.prepare_keep(args)
            meta = json.load(open(os.path.join(cache, "meta.json"), encoding="utf-8"))
            keep = S.open_keep(cache, meta)
            shape, packed = keep.shape, np.array(keep)
            del keep   # a memmap holds its file open, and Windows will not delete an open file
            self.assertEqual(shape, (1, 1, S.TILE, S.TILE))
            self.assertTrue((packed == 1).all(), "every pixel counts, the knots included")

    def test_an_extra_rows_mask_is_its_own(self):
        main_keep = np.zeros((10, 4, 4, 4), dtype=np.uint8)
        extra_keep = np.ones((5, 1, 4, 4), dtype=np.uint8)
        fe = np.array([True, False, True, False])
        out = S.mixed_rows(main_keep, np.array([0, 1, 2, 3]), np.array([0, 1, 2, 3]), extra_keep,
                           np.array([4, 0, 2, 1]), np.array([0, 0, 0, 0]), fe)
        self.assertTrue((out[fe] == 1).all())
        self.assertTrue((out[~fe] == 0).all())


class SplitByScene(unittest.TestCase):
    """A held-out scene (every optics of a held-out seed) gives no cell to training, as the export's lists name it."""

    def test_named_val_scenes_never_train(self):
        sessions = [f"synthetic-cluster|m|s{seed:04d}|{optics}" for seed in (9, 10, 11) for optics in ("lateral", "measured")]
        cells = {(s, x, y): {"subs": ["t"], "master": "m", "half_a": None, "half_b": None} for s in sessions
                 for x in (0, 256) for y in (0, 256)}
        val = [s for s in sessions if "|s0010|" in s]
        train = [s for s in sessions if s not in val]
        train_keys, val_keys, train_s, val_s = S.choose(cells, 0, 0, 16, val_names=val, val_cells_per_session=16, train_names=train)
        self.assertEqual(sorted(val_s), sorted(val))
        self.assertFalse(any("|s0010|" in k[0] for k in train_keys), "no tile of a held-out scene trains")
        self.assertEqual(len(val_keys), 2 * 4, "the held-out scene whole, in both optics")
        self.assertEqual(len(train_keys), 4 * 4)


class ThreePartObjective(unittest.TestCase):
    """The schedule stops on synth4's and the clusters' held-out objectives weighed by their share of the training draw."""

    def test_the_share_counts_a_tile_once_per_draw(self):
        share = S.extra_share(4520, 4, 2592, 1)
        self.assertAlmostEqual(share, 2592 / (4520 * 4 + 2592))

    def test_the_combined_objective_weighs_each_part_by_its_share(self):
        self.assertAlmostEqual(S.combined_objective(2.0e-4, 6.0e-4, 0.125), 0.875 * 2.0e-4 + 0.125 * 6.0e-4)
        self.assertEqual(S.combined_objective(3.0e-4, 9.0e-4, 0.0), 3.0e-4, "no extra share, the main objective alone")

    def test_a_drawn_batch_holds_about_its_share(self):
        rng = np.random.default_rng(1)
        fe = np.concatenate([S.pick_extra(rng, 64, 0.125, 100, 1)[0] for _ in range(400)])
        self.assertAlmostEqual(fe.mean(), 0.125, delta=0.01)


class Refusals(unittest.TestCase):
    def test_an_extra_cache_of_real_subs_is_refused(self):
        with tempfile.TemporaryDirectory() as cache:
            with open(os.path.join(cache, "meta.json"), "w", encoding="utf-8") as fh:
                json.dump({"cells": 1, "injected": False, "train_cells": 1, "draws": 1}, fh)
            with self.assertRaises(SystemExit):
                S.ExtraCache.open(cache, 3, need_sigma=False, need_keep=False)


if __name__ == "__main__":
    unittest.main()
