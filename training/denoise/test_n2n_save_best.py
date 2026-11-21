"""--save-best in n2n_smoke.py (docs/plans/star-remover-training.md, "R4f's read"): the weights at the held-out objective's
best step are written beside --out, which with the gate off stays the last step's as for every run before, and the switch
changes nothing the run computes.

    cd training/denoise && python -m unittest test_n2n_save_best -v
"""
import json
import os
import subprocess
import sys
import tempfile
import time
import unittest

import numpy as np
import torch

import n2n_smoke as S
from test_n2n_init_from import META, args

HERE = os.path.dirname(os.path.abspath(__file__))
# The CPU, so that two runs of one seed agree to the bit on any machine. "-1" and not "": Windows drops a variable set
# to the empty string, and the trainer then takes the GPU (test_the_runs_here_are_on_the_cpu holds this).
ENV = dict(os.environ, CUDA_VISIBLE_DEVICES="-1")


def weights(path):
    saved = torch.load(path, map_location="cpu", weights_only=False)
    return saved["selected_at_step"], saved["model"]


def same(a, b):
    return a.keys() == b.keys() and all(torch.equal(a[k], b[k]) for k in a)


def command(cache, out, steps, min_improve, *extra):
    return [sys.executable, os.path.join(HERE, "n2n_smoke.py"), "--train", "--cache", cache, "--synthetic", "--loss", "l2",
            "--upsample", "--cond-map", "--base", "8", "--batch", "2", "--schedule", "plateau", "--val-every", "5",
            "--patience", "100000", "--max-decays", "1", "--min-improve", str(min_improve), "--gate-every", "0", "--seed", "0",
            "--checkpoint-every", "10", "--resume", "--steps", str(steps), "--out", out, *extra]


def build_cache(root, cache):
    """Six one-tile sessions, four to train on and two held out, prepared into a cache."""
    rng = np.random.default_rng(4)
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
    return subprocess.run([sys.executable, os.path.join(HERE, "n2n_smoke.py"), "--prepare", "--compact", "--root", root,
                           "--cache", cache, "--train-from-list", os.path.join(root, "train.txt"),
                           "--val-from-list", os.path.join(root, "val.txt"), "--cells-per-session", "16",
                           "--val-cells-per-session", "16"], capture_output=True, text=True, cwd=HERE)


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
    """The trainer itself on six one-tile sessions, one cache for the class. The first judged evaluation is the third (step
    15, the first with a full window)."""

    @classmethod
    def setUpClass(cls):
        cls.root = tempfile.TemporaryDirectory()
        cls.cache_dir = tempfile.TemporaryDirectory(ignore_cleanup_errors=True)
        cls.cache = cls.cache_dir.name
        done = build_cache(cls.root.name, cls.cache)
        if done.returncode != 0:
            raise AssertionError(done.stdout + done.stderr)

    @classmethod
    def tearDownClass(cls):
        cls.cache_dir.cleanup()
        cls.root.cleanup()

    def path(self, name):
        return os.path.join(self.cache, name)

    def train(self, out, steps, min_improve, *extra, ok=True):
        done = subprocess.run(command(self.cache, out, steps, min_improve, *extra), capture_output=True, text=True, cwd=HERE, env=ENV)
        if ok:
            self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        return done

    def test_the_best_steps_weights_are_written_beside_the_last_steps(self):
        # --min-improve 0.9 lets only the first judged evaluation count, so the best step (15) is not the last (40).
        self.train("kept.pt", 40, 0.9, "--save-best")
        self.train("to15.pt", 15, 0.9)
        self.train("plain.pt", 40, 0.9)

        last_at, last = weights(self.path("kept.pt"))
        best_at, best = weights(self.path("kept.best.pt"))
        self.assertEqual((last_at, best_at), (40, 15))
        self.assertFalse(same(best, last), "the best step's weights are not the last step's")
        self.assertTrue(same(best, weights(self.path("to15.pt"))[1]), "the best file holds the weights the run had at its best step")

        self.assertTrue(same(last, weights(self.path("plain.pt"))[1]), "the switch changes nothing the run computes")
        self.assertFalse(os.path.exists(self.path("plain.best.pt")), "without the switch there is no best file")
        for name in ("kept", "to15", "plain"):
            self.assertFalse(os.path.exists(self.path(name + ".resume.pt")), "a finished run leaves no checkpoint")

    def test_a_later_best_step_replaces_the_earlier_one(self):
        # With no least gain every lower window is a new best, so the best step moves on from the first judged one. A
        # trainer that kept only its first snapshot would hold step 15's weights here.
        self.train("moving.pt", 40, 0, "--save-best")
        best_at, best = weights(self.path("moving.best.pt"))
        self.assertGreater(best_at, 15, "the objective improved more than once on this cache")

        self.train("to_best.pt", best_at, 0)
        self.assertTrue(same(best, weights(self.path("to_best.pt"))[1]), "the best file holds the LATER best step's weights")

    def test_with_ema_the_best_file_holds_the_averaged_weights_as_the_output_does(self):
        self.train("ema.pt", 40, 0.9, "--save-best", "--ema", "0.9")
        self.train("ema15.pt", 15, 0.9, "--ema", "0.9")

        best_at, best = weights(self.path("ema.best.pt"))
        self.assertEqual(best_at, 15)
        self.assertTrue(same(best, weights(self.path("ema15.pt"))[1]), "the averaged weights at the best step, which --out ships")
        self.assertFalse(same(best, weights(self.path("ema15_raw.pt"))[1]), "and not the live weights of that step")

    def stop_midway(self, name, *extra):
        """A run that cannot end by itself, killed once it has written three checkpoints (steps 10, 20 and 30; the first
        judged evaluation is at 15). The checkpoint is replaced atomically, so whichever one the kill leaves is whole."""
        resume = self.path(name + ".resume.pt")
        with open(self.path(name + ".log"), "w", encoding="utf-8") as log:
            run = subprocess.Popen(command(self.cache, name + ".pt", 10_000_000, 0, *extra), stdout=log, stderr=subprocess.STDOUT, cwd=HERE, env=ENV)
            try:
                seen, deadline = set(), time.monotonic() + 300
                while len(seen) < 3:
                    self.assertIsNone(run.poll(), "the trainer ended by itself")
                    self.assertLess(time.monotonic(), deadline, "no third checkpoint in five minutes")
                    if os.path.exists(resume):
                        seen.add(os.stat(resume).st_mtime_ns)
                    time.sleep(0.05)
            finally:
                run.kill()
                run.wait()
        return torch.load(resume, map_location="cpu", weights_only=False)

    def test_a_run_stopped_midway_carries_its_best_step_through_the_resume(self):
        held = self.stop_midway("stopped", "--save-best")
        self.assertIsNotNone(held["best_state"], "the checkpoint carries the best step's weights")
        best_step = held["plateau"][1]
        self.assertGreaterEqual(best_step, 15)

        # Resumed under a cap it has already passed: it trains no step, so the best file is the checkpoint's alone.
        self.train("stopped.pt", 1, 0, "--save-best")
        best_at, best = weights(self.path("stopped.best.pt"))
        self.assertEqual(best_at, best_step)
        self.assertTrue(same(best, held["best_state"]))

    def test_asked_for_and_nothing_to_write_is_said(self):
        # Ten steps are two evaluations, fewer than the window the plateau rule judges on.
        done = self.train("short.pt", 10, 0, "--save-best")
        self.assertFalse(os.path.exists(self.path("short.best.pt")))
        self.assertIn("--save-best: nothing written", done.stdout)
        self.assertIn("(it read 2)", done.stdout)
        # A run that was never resumed is not told about a resume (#2340).
        self.assertNotIn("resume", done.stdout.split("--save-best: nothing written")[1])

    def test_an_objective_that_is_never_a_number_writes_nothing_beside_the_output_and_names_the_score(self):
        # A learning rate no weight survives: the held-out objective is NaN from its first evaluation, so no step ever
        # improves on the best, which is the second cause the line names. Twenty steps are four evaluations, so the first
        # cause (fewer than three) is not what is read here.
        done = self.train("nan.pt", 20, 0, "--save-best", "--lr", "1e30")
        self.assertEqual(done.stdout.count("held-out nan"), 4, "every one of the four evaluations reads NaN")
        self.assertTrue(os.path.exists(self.path("nan.pt")), "the last step's weights are written as for any run")
        self.assertFalse(os.path.exists(self.path("nan.best.pt")))
        self.assertIn("--save-best: nothing written", done.stdout)
        self.assertIn("(it read 4)", done.stdout)
        self.assertIn("never a finite number (the last score: nan)", done.stdout)
        self.assertNotIn("resume", done.stdout.split("--save-best: nothing written")[1])

    def test_the_runs_here_are_on_the_cpu(self):
        done = self.train("where.pt", 5, 0)
        self.assertIn("device cpu", done.stdout)

    def test_a_resume_of_a_run_made_without_the_switch_says_its_best_step_lies_before_it(self):
        # The checkpoint of a run without the switch holds a best step and no weights for it. Resumed with the switch
        # under a cap already passed, no later step can improve on it, so there is nothing to write and the line says so.
        held = self.stop_midway("unswitched")
        self.assertIsNone(held.get("best_state"))
        best_step = held["plateau"][1]
        self.assertGreaterEqual(best_step, 15)

        done = self.train("unswitched.pt", 1, 0, "--save-best")
        self.assertFalse(os.path.exists(self.path("unswitched.best.pt")))
        self.assertIn("--save-best: nothing written", done.stdout)
        self.assertIn(f"its best step ({best_step}) lies before that resume", done.stdout)

    def test_a_schedule_that_reads_no_held_out_objective_refuses_it(self):
        refused = subprocess.run([sys.executable, os.path.join(HERE, "n2n_smoke.py"), "--train", "--cache", self.cache, "--synthetic",
                                  "--loss", "l2", "--upsample", "--cond-map", "--base", "8", "--batch", "2", "--schedule", "cosine",
                                  "--steps", "10", "--gate-every", "0", "--seed", "0", "--out", "cosine.pt", "--save-best"],
                                 capture_output=True, text=True, cwd=HERE, env=ENV)
        self.assertNotEqual(refused.returncode, 0)
        self.assertIn("--save-best needs --schedule plateau", refused.stdout + refused.stderr)
        self.assertFalse(os.path.exists(self.path("cosine.pt")))


if __name__ == "__main__":
    unittest.main()
