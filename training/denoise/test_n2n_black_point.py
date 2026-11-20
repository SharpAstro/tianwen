"""The stretch's black point from an export to a model's contract (#2309): what `--prepare` reads off an export's rows, what
an extra cache has to agree on, and what `n2n_export.py` writes for the remover to stretch a frame by.

    cd training/denoise && python -m unittest test_n2n_black_point -v

numpy only, but for the contract itself, whose writer imports the operator module and with it torch.
"""
import importlib.util
import json
import os
import tempfile
import unittest

import numpy as np

import n2n_export as E
import n2n_smoke as S


def write_rows(root, values):
    """A degradations.jsonl whose rows carry these StretchBlackPointSigma values; a value of ... leaves the field out."""
    with open(os.path.join(root, "degradations.jsonl"), "w", encoding="utf-8") as fh:
        for i, value in enumerate(values):
            row = {"Tile": f"tiles/t{i}.f16", "SessionId": "s"}
            if value is not ...:
                row["StretchBlackPointSigma"] = value
            fh.write(json.dumps(row) + "\n")


def write_cache(cache, black_point=...):
    """The smallest synthetic cache an extra cache opens: one training cell, its truth and one draw."""
    meta = {"cells": 1, "slots": 2, "injected": True, "train_cells": 1, "draws": 1, "channels": 3}
    if black_point is not ...:
        meta[S.BLACK_POINT_KEY] = black_point
    with open(os.path.join(cache, "meta.json"), "w", encoding="utf-8") as fh:
        json.dump(meta, fh)
    np.zeros((1, 2, 3, S.TILE, S.TILE), dtype=np.float16).tofile(os.path.join(cache, "tiles.f16"))


class WhatAnExportSays(unittest.TestCase):
    def test_an_export_with_no_rows_file_is_stretched_from_the_minimum(self):
        with tempfile.TemporaryDirectory() as root:
            self.assertIsNone(S.load_black_point_sigma(root))

    def test_rows_that_do_not_carry_the_field_are_the_minimum(self):
        # Every export before 2026-11-21: the field does not exist, or is null.
        with tempfile.TemporaryDirectory() as root:
            write_rows(root, [..., ..., None])
            self.assertIsNone(S.load_black_point_sigma(root))

    def test_rows_at_one_depth_give_that_depth(self):
        with tempfile.TemporaryDirectory() as root:
            write_rows(root, [40.0, 40.0, 40.0])
            self.assertEqual(S.load_black_point_sigma(root), 40.0)

    def test_rows_at_two_depths_stop_the_prepare(self):
        with tempfile.TemporaryDirectory() as root:
            write_rows(root, [40.0, 60.0])
            with self.assertRaises(SystemExit) as stopped:
                S.load_black_point_sigma(root)
            self.assertIn("more than one black point", str(stopped.exception))

    def test_a_depth_beside_rows_from_the_minimum_stops_the_prepare(self):
        with tempfile.TemporaryDirectory() as root:
            write_rows(root, [40.0, ...])
            with self.assertRaises(SystemExit):
                S.load_black_point_sigma(root)


class TwoCachesOneDomain(unittest.TestCase):
    def test_an_extra_cache_from_another_black_point_is_refused(self):
        with tempfile.TemporaryDirectory() as cache:
            write_cache(cache)
            with self.assertRaises(SystemExit) as stopped:
                S.ExtraCache.open(cache, 3, need_sigma=False, need_keep=False, black_point=40.0)
            self.assertIn("each channel's minimum", str(stopped.exception))
            self.assertIn("40 sky-noise sigma", str(stopped.exception))

    def test_an_extra_cache_at_a_depth_is_refused_by_a_main_cache_from_the_minimum(self):
        with tempfile.TemporaryDirectory() as cache:
            write_cache(cache, black_point=40.0)
            with self.assertRaises(SystemExit):
                S.ExtraCache.open(cache, 3, need_sigma=False, need_keep=False)

    def test_two_caches_at_one_depth_join(self):
        with tempfile.TemporaryDirectory() as cache:
            write_cache(cache, black_point=40.0)
            extra = S.ExtraCache.open(cache, 3, need_sigma=False, need_keep=False, black_point=40.0)
            self.assertEqual(extra.n_train, 1)
            del extra

    def test_two_caches_from_the_minimum_join_as_they_always_did(self):
        with tempfile.TemporaryDirectory() as cache:
            write_cache(cache)
            extra = S.ExtraCache.open(cache, 3, need_sigma=False, need_keep=False)
            self.assertEqual(extra.n_train, 1)
            del extra


class WhatTheContractSays(unittest.TestCase):
    def test_the_depth_is_read_off_the_cache(self):
        with tempfile.TemporaryDirectory() as cache:
            write_cache(cache, black_point=40.0)
            self.assertEqual(E.cache_black_point(cache), 40.0)
        with tempfile.TemporaryDirectory() as cache:
            write_cache(cache)
            self.assertIsNone(E.cache_black_point(cache))
        self.assertIsNone(E.cache_black_point(None))

    @unittest.skipUnless(importlib.util.find_spec("torch"), "the contract's writer imports the operator module, which needs torch")
    def test_a_model_of_a_cache_at_a_depth_states_it_and_one_from_the_minimum_says_nothing(self):
        with tempfile.TemporaryDirectory() as folder:
            model = os.path.join(folder, "model.onnx")
            with open(model, "wb") as fh:
                fh.write(b"weights")
            at_depth, plain = os.path.join(folder, "at-depth"), os.path.join(folder, "plain")
            os.mkdir(at_depth)
            os.mkdir(plain)
            write_cache(at_depth, black_point=40.0)
            write_cache(plain)

            with open(E.write_contract(model, at_depth), encoding="utf-8") as fh:
                stated = json.load(fh)
            with open(E.write_contract(model, plain), encoding="utf-8") as fh:
                silent = json.load(fh)
            with open(E.write_contract(model), encoding="utf-8") as fh:
                no_cache = json.load(fh)

            self.assertEqual(stated["stretchBlackPointSigma"], 40.0)
            self.assertNotIn("stretchBlackPointSigma", silent)
            self.assertEqual(silent, no_cache)
            self.assertEqual({k: v for k, v in stated.items() if k != "stretchBlackPointSigma"}, silent)


if __name__ == "__main__":
    unittest.main()
