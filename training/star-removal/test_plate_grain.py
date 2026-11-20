"""plate_grain.py: what it calls absent, that it hands a canvas ring back as it came, and the diagnostic's own-noise switch.

    cd training/star-removal && python -m unittest test_plate_grain -v
"""
import unittest

import numpy as np
from scipy.ndimage import median_filter

import plate_grain as G


class ZeroRing(unittest.TestCase):
    def test_a_ring_of_zeros_at_the_border_is_absent(self):
        plate = np.full((3, 40, 50), 5.0, np.float32)
        plate[:, :6, :] = 0
        plate[:, :, :4] = 0
        ring = G.zero_ring(plate)
        self.assertTrue(ring[:6].all() and ring[:, :4].all())
        self.assertEqual(int(ring.sum()), 6 * 50 + 34 * 4)

    def test_an_island_of_zeros_inside_the_frame_is_a_measurement(self):
        plate = np.full((1, 40, 50), 5.0, np.float32)
        plate[0, 18:22, 20:26] = 0
        self.assertFalse(G.zero_ring(plate).any())

    def test_a_pixel_that_is_zero_in_one_channel_only_is_not_absent(self):
        plate = np.full((3, 40, 50), 5.0, np.float32)
        plate[0, :6, :] = 0
        self.assertFalse(G.zero_ring(plate).any())

    def test_a_nan_is_left_to_its_channel(self):
        plate = np.full((3, 40, 50), 5.0, np.float32)
        plate[1, -3:, :] = np.nan
        self.assertFalse(G.zero_ring(plate).any(), "the rule tests each channel's NaN itself, as its first run did")


class RingHandedBack(unittest.TestCase):
    def plate(self, ring_value):
        rng = np.random.default_rng(3)
        plate = (1000.0 + rng.normal(0, 10, (1, 1200, 1200))).astype(np.float32)
        for edge in (np.s_[:, :30, :], np.s_[:, -30:, :], np.s_[:, :, :30], np.s_[:, :, -30:]):
            plate[edge] = ring_value
        return plate

    def run_rule(self, plate):
        dens = np.full(plate.shape[1:], 0.02, np.float32)     # crowded everywhere, so the rule acts on every pixel it may
        return G.grain_out(plate, dens, "test")

    def test_a_ring_of_zeros_stays_zero_and_the_sky_beside_it_is_not_pulled_down(self):
        plate = self.plate(0.0)
        out, note = self.run_rule(plate)
        self.assertTrue(note and note[0].startswith("grain"), f"the rule ran: {note}")
        ring = G.zero_ring(plate)
        self.assertTrue((out[0][ring] == 0).all(), "the ring is handed back as it came")
        self.assertGreater(float(out[0][~ring].min()), 900.0, "no sky pixel is mixed with the ring's zeros")
        self.assertFalse(np.array_equal(out[0][~ring], plate[0][~ring]), "and the rule did act on the sky")

    def test_a_ring_of_nan_gives_the_same_sky_as_a_ring_of_zeros(self):
        zeros, _ = self.run_rule(self.plate(0.0))
        nans, _ = self.run_rule(self.plate(np.nan))
        inside = np.s_[30:-30, 30:-30]
        np.testing.assert_array_equal(zeros[0][inside], nans[0][inside])
        self.assertTrue(np.isnan(nans[0][:30]).all())


class OwnNoise(unittest.TestCase):
    """fresh_noise=False, the switch grain_rule_post.py --own-noise passes: the plate's own residual under 7 px stands
    where the drawn noise would, so the result differs from the plate by the smoothing alone. No export passes it."""

    @classmethod
    def setUpClass(cls):
        rng = np.random.default_rng(5)
        cls.plate = (1000.0 + rng.normal(0, 10, (1, 1200, 1200))).astype(np.float32)
        cls.dens = np.full((1200, 1200), 0.02, np.float32)     # crowded everywhere, and plain noise: the rule acts fully
        cls.default, cls.note = G.grain_out(cls.plate, cls.dens, "test")
        cls.own, cls.own_note = G.grain_out(cls.plate, cls.dens, "test", fresh_noise=False)
        cls.inside = np.s_[100:-100, 100:-100]

    def test_the_default_is_the_fresh_draw(self):
        fresh, _ = G.grain_out(self.plate, self.dens, "test", fresh_noise=True)
        np.testing.assert_array_equal(self.default, fresh)

    def test_the_rule_smooths_the_same_share_either_way(self):
        self.assertEqual(self.note, self.own_note)
        self.assertIn("smoothed 100.0%", self.note[0])

    def test_with_the_switch_off_the_change_is_the_smoothing_alone(self):
        # Where the rule acts fully the plate's own values less its 7 px median ride on the 31 px glow, so what changed
        # is the glow less the 7 px median and nothing drawn.
        p7 = median_filter(self.plate[0], 7)
        np.testing.assert_allclose((self.own - self.plate)[0][self.inside], (G.base_of(p7) - p7)[self.inside], atol=1e-2)

    def test_with_the_switch_off_the_plates_own_fine_residual_stays(self):
        fine = lambda a: (a - median_filter(a, 7))[self.inside].ravel()
        plate = fine(self.plate[0])
        self.assertGreater(float(np.corrcoef(fine(self.own[0]), plate)[0, 1]), 0.95)
        self.assertLess(abs(float(np.corrcoef(fine(self.default[0]), plate)[0, 1])), 0.1, "a fresh draw shares nothing with the plate's noise")


if __name__ == "__main__":
    unittest.main()
