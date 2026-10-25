"""The torch speckle penalty (`n2n_speckle.py`) against the C# measure it ports (`StarlessSpeckles`).

    cd training/denoise && python -m unittest test_n2n_speckle -v

The fixture in `fixtures/speckle/` is written by the C# (`SpecklePortFixtureProbe`, TianWen.Lib.Tests, opt-in through
TIANWEN_SPECKLE_FIXTURE_DIR): a 160 x 160 correlated-noise plane with dipoles dug into some sites, an absent canvas ring
and blob, the noise cap, and what `StarlessSpeckles` reads at 66 sites (flag, ring median, sigma). It is deterministic,
so it regenerates byte-identical until the measure changes. To regenerate it after a change to `StarlessSpeckles`:

    set TIANWEN_SPECKLE_FIXTURE_DIR=%CD%/training/denoise/fixtures/speckle
    dotnet test src/TianWen.Lib.Tests --filter FullyQualifiedName~SpecklePortFixtureProbe

Tolerances. The flags are exact. The ring median is a selected element, so it is exact; sigma is 1.4826f x a selected
element, float32 in both, so it agrees to 1e-6 relative. The noise cap goes through a double bisection and a double
interpolation on both sides and agrees to 1e-4 relative (what a different summation order of the cell spreads moves).

Needs torch (CPU is enough); without it the torch cases are skipped, not failed.
"""
import json
import os
import unittest

import numpy as np

try:
    import torch

    import n2n_speckle as SP
except ImportError:  # pragma: no cover - a machine with no torch wheel (win-arm64 on Python 3.13+)
    torch = None

HERE = os.path.dirname(os.path.abspath(__file__))
FIXTURE = os.path.join(HERE, "fixtures", "speckle")

SIGMA_RTOL = 1e-6
CAP_RTOL = 1e-4


def load_fixture():
    with open(os.path.join(FIXTURE, "fixture.json"), encoding="utf-8") as fh:
        meta = json.load(fh)
    size, n = meta["size"], meta["sites"]

    def read(name, dtype, shape):
        values = np.fromfile(os.path.join(FIXTURE, name), dtype=dtype)
        assert values.size == int(np.prod(shape)), f"{name}: {values.size} values, expected {shape}"
        return values.reshape(shape)

    return meta, {
        "plane": read("plane.f32", "<f4", (size, size)),
        "absent": read("absent.u8", "u1", (size, size)).astype(bool),
        "cap": read("cap.f32", "<f4", (size, size)),
        "sites": read("sites.f32", "<f4", (n, 2)),
        "flags": read("flags.i8", "i1", (n,)),
        "median": read("median.f32", "<f4", (n,)),
        "sigma": read("sigma.f32", "<f4", (n,)),
    }


@unittest.skipIf(torch is None, "torch is not installed")
class SpecklePortParity(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.meta, cls.fx = load_fixture()
        cls.plane = torch.from_numpy(cls.fx["plane"])[None]
        cls.absent = torch.from_numpy(cls.fx["absent"])[None]
        cls.cs_cap = torch.from_numpy(cls.fx["cap"])[None]
        xy = torch.from_numpy(cls.fx["sites"])
        cls.sites = torch.cat([torch.zeros(len(xy), 1), xy], dim=1)

    def test_the_constants_are_the_measures(self):
        self.assertEqual(SP.SPECKLE_SIGMA, self.meta["speckleSigma"])
        self.assertEqual(SP.CORE_RADIUS, self.meta["coreRadius"])
        self.assertEqual(SP.SKY_INNER, self.meta["skyInner"])
        self.assertEqual(SP.SKY_OUTER, self.meta["skyOuter"])
        self.assertEqual(SP.CAP_BLOCK, self.meta["capBlock"])

    def test_the_fixture_exercises_every_outcome(self):
        flags = self.fx["flags"]
        self.assertGreater((flags == 1).sum(), 10)
        self.assertGreater((flags == 0).sum(), 10)
        self.assertGreaterEqual((flags == -1).sum(), 2)

    def test_the_noise_cap_is_the_csharp_cap(self):
        cap = SP.noise_cap(self.plane, self.absent)
        cs = self.cs_cap
        self.assertTrue(torch.equal(torch.isinf(cap), torch.isinf(cs)))
        fin = torch.isfinite(cs)
        self.assertGreater(int(fin.sum()), 0, "the fixture's cap is the model's, not +inf everywhere")
        np.testing.assert_allclose(cap[fin].numpy(), cs[fin].numpy(), rtol=CAP_RTOL, atol=0)

    def read(self, cap):
        return SP.read_sites(self.plane, self.sites, cap, self.absent)

    def test_the_flags_are_exactly_the_sites_the_csharp_flagged(self):
        flags = self.fx["flags"]
        for label, cap in (("C# cap", self.cs_cap), ("torch cap", SP.noise_cap(self.plane, self.absent))):
            reads = self.read(cap)
            np.testing.assert_array_equal(reads.valid.numpy(), flags >= 0, err_msg=f"read sites, {label}")
            np.testing.assert_array_equal(reads.speckled.numpy(), flags == 1, err_msg=f"speckled sites, {label}")

    def test_the_ring_median_and_sigma_are_the_csharp_ones(self):
        reads = self.read(self.cs_cap)
        valid = self.fx["flags"] >= 0
        np.testing.assert_array_equal(reads.median.numpy()[valid], self.fx["median"][valid])
        np.testing.assert_allclose(reads.sigma.numpy()[valid], self.fx["sigma"][valid], rtol=SIGMA_RTOL, atol=0)
        self.assertTrue(np.isnan(reads.median.numpy()[~valid]).all())
        self.assertTrue(np.isnan(reads.sigma.numpy()[~valid]).all())

    def test_a_site_has_a_penalty_exactly_when_the_csharp_flagged_it(self):
        reads = self.read(self.cs_cap)
        flags = self.fx["flags"]
        penalty = reads.penalty.numpy()
        self.assertTrue((penalty[flags == 1] > 0).all())
        self.assertTrue((penalty[flags != 1] == 0).all())
        total = SP.speckle_penalty(self.plane, self.sites, self.cs_cap, self.absent)
        self.assertAlmostEqual(float(total), float(penalty.sum()), delta=1e-6 * float(total))

    def clean_plane(self):
        """The fixture's plane with every flagged site's core put back to the C#'s own ring median."""
        plane = self.plane.clone()
        for i in np.flatnonzero(self.fx["flags"] == 1):
            x, y = self.fx["sites"][i]
            x0, y0 = int(np.round(x)), int(np.round(y))
            for yy in range(y0 - 3, y0 + 4):
                for xx in range(x0 - 3, x0 + 4):
                    if (xx - x) ** 2 + (yy - y) ** 2 <= 9.0 and not self.fx["absent"][yy, xx]:
                        plane[0, yy, xx] = float(self.fx["median"][i])
        return plane

    def test_a_clean_plane_has_no_penalty_and_no_gradient(self):
        plane = self.clean_plane().requires_grad_(True)
        reads = SP.read_sites(plane, self.sites, self.cs_cap, self.absent)
        self.assertFalse(bool(reads.speckled.any()), f"{int(reads.speckled.sum())} sites still speckled")
        total = reads.penalty.sum()
        self.assertEqual(float(total.detach()), 0.0)
        total.backward()
        self.assertTrue(bool((plane.grad == 0).all()))

    def test_a_dug_core_has_a_penalty_and_a_gradient_that_lifts_it(self):
        plane = self.clean_plane()
        # A readable site on the smooth left half, the middle pixel of its core dug 10 sigma.
        reads = SP.read_sites(plane, self.sites, self.cs_cap, self.absent)
        i = next(j for j in range(len(self.sites)) if bool(reads.valid[j]) and self.fx["sites"][j][0] < 70)
        x, y = self.fx["sites"][i]
        x0, y0 = int(np.round(x)), int(np.round(y))
        plane[0, y0, x0] -= 10 * float(reads.sigma[i])
        plane.requires_grad_(True)
        dug = SP.read_sites(plane, self.sites, self.cs_cap, self.absent)
        self.assertTrue(bool(dug.speckled[i]))
        self.assertGreater(float(dug.penalty[i].detach()), 0.0)
        total = dug.penalty.sum()
        total.backward()
        grad = plane.grad[0]
        self.assertLess(float(grad[y0, x0]), 0.0, "descending the penalty lifts the dug pixel")
        # The statistics are detached: only core pixels that fall short carry gradient.
        carrying = int((grad != 0).sum())
        self.assertGreater(carrying, 0)
        self.assertLessEqual(carrying, 29 * int(dug.speckled.sum()), "no more than a core's pixels per speckled site")
        # A step down the gradient lowers the penalty.
        stepped = (plane.detach() - 0.5 * plane.grad / grad.abs().max() * float(dug.sigma[i])).requires_grad_(False)
        after = SP.speckle_penalty(stepped, self.sites, self.cs_cap, self.absent)
        self.assertLess(float(after), float(total))

    def test_the_statistics_gradient_is_finite_and_reaches_the_ring_when_asked(self):
        plane = self.plane.clone().requires_grad_(True)
        SP.speckle_penalty(plane, self.sites, self.cs_cap, self.absent, detach_stats=False).backward()
        self.assertTrue(bool(torch.isfinite(plane.grad).all()), "an unread site (inf in its sort) must not leak NaN")
        detached = self.plane.clone().requires_grad_(True)
        SP.speckle_penalty(detached, self.sites, self.cs_cap, self.absent).backward()
        self.assertGreater(int((plane.grad != 0).sum()), int((detached.grad != 0).sum()))

    def test_tiles_are_read_independently_in_a_batch(self):
        both = torch.cat([self.plane, self.clean_plane()])
        absent = torch.cat([self.absent, self.absent])
        cap = torch.cat([self.cs_cap, self.cs_cap])
        sites = torch.cat([self.sites, self.sites + torch.tensor([1.0, 0.0, 0.0])])
        reads = SP.read_sites(both, sites, cap, absent)
        n = len(self.sites)
        np.testing.assert_array_equal(reads.speckled[:n].numpy(), self.fx["flags"] == 1)
        self.assertFalse(bool(reads.speckled[n:].any()))
        self.assertTrue(bool((reads.penalty[n:] == 0).all()))


if __name__ == "__main__":
    unittest.main()
