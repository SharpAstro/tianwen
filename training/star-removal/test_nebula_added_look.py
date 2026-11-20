"""nebula_added_look.py on the inputs that used to stop it: a plate that adds nothing new, and a frame with no nebula.

    cd training/star-removal && python -m unittest test_nebula_added_look -v
"""
import os, subprocess, sys, tempfile, unittest

import numpy as np
from astropy.io import fits

HERE = os.path.dirname(os.path.abspath(__file__))


def frame(nebula):
    """A 400 x 400 three-channel frame of noise 1 on a sky of 100, with a 200 px square of 400 at its middle if asked."""
    rng = np.random.default_rng(3)
    a = (100 + rng.normal(0, 1, (3, 400, 400))).astype(np.float32)
    if nebula:
        a[:, 100:300, 100:300] += 300
    return a


class Runs(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.dir.cleanup)

    def path(self, name, data):
        p = os.path.join(self.dir.name, name)
        fits.PrimaryHDU(data).writeto(p)
        return p

    def run_look(self, master, base, plate):
        out = os.path.join(self.dir.name, "sheet.png")
        r = subprocess.run([sys.executable, "-I", os.path.join(HERE, "nebula_added_look.py"), master, base, plate, out, "200", "200", "200"],
                           capture_output=True, text=True)
        return r, out

    def test_a_plate_that_adds_nothing_new_says_so_and_still_writes_the_sheet(self):
        m = self.path("m.fits", frame(True))
        r, out = self.run_look(m, m, m)
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("no new pixels", r.stdout)
        self.assertTrue(os.path.getsize(out) > 0)

    def test_a_fill_inside_the_nebula_is_counted_and_its_darkness_printed(self):
        a = frame(True)
        hole = a.copy(); hole[:, 190:210, 190:210] -= 30          # a dark pocket in the input
        m = self.path("m.fits", hole)
        base = self.path("base.fits", hole)
        plate = self.path("plate.fits", a)                        # the plate fills it
        r, out = self.run_look(m, base, plate)
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("newly added pixels", r.stdout)
        self.assertIn("median -", r.stdout.split("newly added pixels")[1])   # the input is dark there
        self.assertTrue(os.path.getsize(out) > 0)

    def test_a_frame_with_no_nebula_says_so_and_still_writes_the_sheet(self):
        m = self.path("m.fits", frame(False))
        r, out = self.run_look(m, m, m)
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("no nebula", r.stdout)
        self.assertTrue(os.path.getsize(out) > 0)


if __name__ == "__main__":
    unittest.main()
