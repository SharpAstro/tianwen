"""bright_plate_cells.py's gate (R4g): which bright cells take the stored plate as their target.

    cd training/star-removal && python -m unittest test_bright_plate_cells -v
"""
import unittest

import numpy as np

import bright_plate_cells as B


class Gate(unittest.TestCase):
    def frame(self):
        return np.zeros((600, 600), dtype=bool)

    def test_a_cell_the_builder_hardly_touched_passes(self):
        inpainted, subtracted = self.frame(), self.frame()
        inpainted[10:20, 10:60] = True          # 500 of 65,536 px: 0.76 %
        subtracted[100:140, 100:200] = True     # 4,000 px: 6.1 %
        r = B.cell_readings(inpainted, subtracted, np.array([50.0]), np.array([50.0]), np.array([400.0]), np.array([False]), 0, 0)
        self.assertAlmostEqual(r[0], 100.0 * 500 / 65536)
        self.assertAlmostEqual(r[1], 100.0 * 4000 / 65536)
        self.assertEqual(r[2], 0, "a subtracted star is not one left")
        self.assertTrue(B.passes(*r, 10.0, 20.0, 0))

    def test_a_cluster_core_fails_on_what_was_subtracted(self):
        inpainted, subtracted = self.frame(), self.frame()
        subtracted[0:256, 0:140] = True         # 54.7 %
        r = B.cell_readings(inpainted, subtracted, np.array([]), np.array([]), np.array([]), np.array([], dtype=bool), 0, 0)
        self.assertFalse(B.passes(*r, 10.0, 20.0, 0))

    def test_a_bright_star_left_in_the_cell_fails_it_and_one_outside_does_not(self):
        inpainted, subtracted = self.frame(), self.frame()
        xs, ys = np.array([120.0, 300.0, 130.0]), np.array([120.0, 120.0, 130.0])
        significance, left = np.array([250.0, 900.0, 40.0]), np.array([True, True, True])
        inside = B.cell_readings(inpainted, subtracted, xs, ys, significance, left, 0, 0)
        self.assertEqual(inside[2], 1, "the one at 250 sigma; the one at 40 is under the bar, the one at x 300 outside")
        self.assertFalse(B.passes(*inside, 10.0, 20.0, 0))
        beside = B.cell_readings(inpainted, subtracted, xs, ys, significance, left, 0, 256)
        self.assertEqual(beside[2], 0)
        self.assertTrue(B.passes(*beside, 10.0, 20.0, 0))

    def test_a_session_left_out_by_name_takes_its_flip_sides_with_it(self):
        named = ["Lagoon-and-Trifid/2023-06-16"]
        self.assertTrue(B.is_excluded("ZWO-ASI533MC-Pro/Optolong-L-eNhance/Lagoon-and-Trifid/2023-06-16|cam|obj|filter", named))
        self.assertTrue(B.is_excluded("ZWO-ASI533MC-Pro/Optolong-L-eNhance/lagoon-and-trifid/2023-06-16|cam|obj|filter|flip=a", named))
        self.assertFalse(B.is_excluded("Uranus-C-IMX585/Optolong-L-Ultimate-3nm/Lagoon-and-Trifid/2023-08-09|cam|obj|filter", named))
        self.assertFalse(B.is_excluded("anything", []))

    def test_the_bars_are_inclusive(self):
        self.assertTrue(B.passes(10.0, 20.0, 0, 10.0, 20.0, 0))
        self.assertFalse(B.passes(10.1, 20.0, 0, 10.0, 20.0, 0))
        self.assertFalse(B.passes(10.0, 20.1, 0, 10.0, 20.0, 0))


if __name__ == "__main__":
    unittest.main()
