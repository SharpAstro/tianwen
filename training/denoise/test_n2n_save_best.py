"""--save-best in n2n_smoke.py (docs/plans/star-remover-training.md, "R4f's read"): the weights at the held-out objective's
best step are written beside --out, which stays the last step's as for every run before, and the switch changes nothing the
run computes.

    cd training/denoise && python -m unittest test_n2n_save_best -v
"""
import json
import os
import subprocess
import sys
import tempfile
import unittest

import numpy as np
import torch

import n2n_smoke as S
from test_n2n_init_from import META, args

HERE = os.path.dirname(os.path.abspath(__file__))


def weights(path):
    saved = torch.load(path, map_location="cpu", weights_only=False)
    return saved["selected_at_step"], saved["model"]


def same(a, b):
    return a.keys() == b.keys() and all(torch.equal(a[k], b[k]) for k in a)


class Helpers(unittest.TestCase):
    def test_a_snapshot_does_not_follow_the_weights_it_was_taken_from(self):
        layer = torch.nn.Linear(3, 2)
        before = {k: v.clone() for k, v in layer.state_dict().items()}
        kept = S.snapshot_state(layer)
        with torch.no_grad():
            for p in layer.parameters():
                p.add_(1.0)
        self.assertTrue(same(kept, before))
        self.assertFalse(same(kept, layer.state_dict()))
        self.assertEqual({t.device.type for t in kept.values()}, {"cpu"})

    def test_the_best_weights_go_beside_the_output_under_a_name_of_their_own(self):
        self.assertEqual(S.best_path_of("r4f_synth31_s1.pt"), "r4f_synth31_s1.best.pt")
        self.assertNotIn(S.best_path_of("a.pt"), ("a.pt", "a.resume.pt", "a_raw.pt"))

    def test_the_switch_is_not_part_of_what_makes_two_invocations_the_same_run(self):
        self.assertEqual(S.resume_fingerprint(args(save_best=True), META), S.resume_fingerprint(args(save_best=False), META))
        self.assertNotIn("save_best", S.resume_fingerprint(args(save_best=True), META))


class EndToEnd(unittest.TestCase):
    """The trainer itself on six one-tile sessions. --min-improve 0.9 lets only the first judged evaluation (the third,
    step 15, the first with a full window) count as an improvement, so the best step is not the last one."""

    def train(self, cache, out, steps, *extra):
        # The CPU, so that two runs of one seed agree to the bit on any machine.
        env = dict(os.environ, CUDA_VISIBLE_DEVICES="")
        done = subprocess.run([sys.executable, os.path.join(HERE, "n2n_smoke.py"), "--train", "--cache", cache, "--synthetic",
                               "--loss", "l2", "--upsample", "--cond-map", "--base", "8", "--batch", "2", "--schedule", "plateau",
                               "--val-every", "5", "--patience", "100000", "--max-decays", "1", "--min-improve", "0.9",
                               "--gate-every", "0", "--seed", "0", "--checkpoint-every", "10", "--resume", "--steps", str(steps),
                               "--out", out, *extra], capture_output=True, text=True, cwd=HERE, env=env)
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        return done.stdout

    def test_the_best_steps_weights_are_written_beside_the_last_steps(self):
        rng = np.random.default_rng(4)
        with tempfile.TemporaryDirectory() as root, tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as cache:
            sessions, rows = [f"synthetic-cluster|m|s{n:04d}|measured" for n in range(1, 7)], []
            for session in sessions:
                slug = session.replace("|", "_")
                os.makedirs(os.path.join(root, "tiles", slug))
                scene = rng.random((S.CH, S.TILE, S.TILE)) * 0.2
                for frame in ("master", "deg000"):
                    rel = f"tiles/{slug}/x0_y0_{frame}.f16"
                    tile = scene if frame == "master" else scene + rng.normal(0, 0.02, scene.shape)
                    tile.astype("<f2").tofile(os.path.join(root, rel))
                    np.full((S.TILE, S.TILE), 0.02).astype("<f2").tofile(os.path.join(root, S.sigma_path_for(rel)))
                    rows.append({"Tile": rel, "SessionId": session, "Frame": frame, "CellX": 0, "CellY": 0})
            with open(os.path.join(root, "tiles-manifest.jsonl"), "w", encoding="utf-8") as fh:
                fh.write("".join(json.dumps(r) + "\n" for r in rows))
            for name, part in (("train.txt", sessions[:4]), ("val.txt", sessions[4:])):
                with open(os.path.join(root, name), "w", encoding="utf-8") as fh:
                    fh.write("".join(s + "\n" for s in part))
            done = subprocess.run([sys.executable, os.path.join(HERE, "n2n_smoke.py"), "--prepare", "--compact", "--root", root,
                                   "--cache", cache, "--train-from-list", os.path.join(root, "train.txt"),
                                   "--val-from-list", os.path.join(root, "val.txt"), "--cells-per-session", "16",
                                   "--val-cells-per-session", "16"], capture_output=True, text=True, cwd=HERE)
            self.assertEqual(done.returncode, 0, done.stdout + done.stderr)

            self.train(cache, "kept.pt", 40, "--save-best")
            self.train(cache, "to15.pt", 15)
            self.train(cache, "plain.pt", 40)

            last_at, last = weights(os.path.join(cache, "kept.pt"))
            best_at, best = weights(os.path.join(cache, "kept.best.pt"))
            self.assertEqual((last_at, best_at), (40, 15))
            self.assertFalse(same(best, last), "the best step's weights are not the last step's")
            self.assertTrue(same(best, weights(os.path.join(cache, "to15.pt"))[1]),
                            "the best file holds the weights the run had at its best step")

            self.assertTrue(same(last, weights(os.path.join(cache, "plain.pt"))[1]), "the switch changes nothing the run computes")
            self.assertFalse(os.path.exists(os.path.join(cache, "plain.best.pt")), "without the switch there is no best file")
            for name in ("kept", "to15", "plain"):
                self.assertFalse(os.path.exists(os.path.join(cache, name + ".resume.pt")), "a finished run leaves no checkpoint")


if __name__ == "__main__":
    unittest.main()
