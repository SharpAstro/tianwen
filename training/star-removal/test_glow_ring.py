"""Pins glow_ring.py on synthetic masters: no GPU, no owner data. Run with `python -I test_glow_ring.py`; exit 0 is a pass."""
import re
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))        # -I leaves the script's folder off sys.path
import numpy as np
from astropy.io import fits

import glow_ring as g

SKY, NOISE, SIZE = 0.02, 0.002, 500


def moffat(shape, y, x, peak=3.0, alpha=3.0, beta=2.5):
    yy, xx = np.mgrid[:shape[0], :shape[1]]
    return peak * (1 + ((yy - y) ** 2 + (xx - x) ** 2) / alpha ** 2) ** -beta


def master(stars, seed=1):
    """Three-channel noisy sky with a Moffat at each (y, x, peak), clipped at 1 (peak 1 is the master's maximum)."""
    rng = np.random.default_rng(seed)
    img = np.empty((3, SIZE, SIZE))
    for c in range(3):
        img[c] = SKY + NOISE * rng.standard_normal((SIZE, SIZE))
        for y, x, peak in stars:
            img[c] += moffat((SIZE, SIZE), y, x, peak)
    return np.clip(img, 0.0, 1.0)


def disc_pixels(mask):
    return int(mask.sum())


def test_selection():
    # clipped and isolated: kept; unclipped (peak 0.7): not; two within 60 px: both not; near the edge: not
    m = master([(150, 150, 3.0), (150, 350, 0.7), (350, 150, 3.0), (350, 190, 3.0), (30, 300, 3.0), (350, 400, 3.0)])
    div, mins, betas, cov = g.stretch_params(m)
    assert div == 1.0
    stars = g.select_stars(m, div, cov)
    assert sorted(stars) == [(150, 150), (350, 400)], stars


def test_zero_canvas_ring_is_not_covered():
    m = master([(250, 250, 3.0)])
    m[:, :20, :] = 0.0                           # a ring reachable from the border
    m[:, 100, 100] = 0.0                         # an interior zero is a pixel
    cov = g.covered_mask(m[0])
    assert not cov[5, 5] and cov[100, 100]
    div, mins, betas, covered = g.stretch_params(m)
    assert abs(mins[0] - m[0][cov].min()) < 1e-12 and mins[0] == 0.0      # the interior zero IS the covered minimum


def expected_flat_excess(m, delta, ring_delta=0.0):
    """Independent arithmetic: a linear step `delta` over the annulus (and `ring_delta` over the sky ring) of a plate that
    is the input's own sky, through the same stretch, summed in the master's sigma."""
    div, mins, betas, cov = g.stretch_params(m)
    sigma = g.noise_sigma(g.stretch(m, div, mins, betas), cov)
    n = disc_pixels(g.ANNULUS)
    out = []
    for c in range(3):
        base = g.mtf(betas[c], SKY / div - mins[c])
        up = g.mtf(betas[c], (SKY + delta) / div - mins[c])
        ring = g.mtf(betas[c], (SKY + ring_delta) / div - mins[c])
        out.append(n * (up - ring) / sigma[c])
    return np.array(out)


def flat_plate(shape, y, x, delta, ring_delta=0.0):
    plate = np.full(shape, SKY)
    rr = np.hypot(*np.mgrid[:shape[-2], :shape[-1]] - np.array([y, x])[:, None, None])
    plate[:, (rr >= g.INNER) & (rr < g.OUTER)] += delta
    plate[:, (rr >= g.SKY_IN) & (rr < g.SKY_OUT)] += ring_delta
    return plate


def test_known_halo_gives_its_known_excess():
    m = master([(250, 250, 3.0)])
    for delta in (0.005, 0.02):
        stars, ex, own = g.measure(m, flat_plate(m.shape, 250, 250, delta))
        assert stars == [(250, 250)]
        want = expected_flat_excess(m, delta)
        assert np.allclose(ex[0], want, rtol=1e-6), (ex[0], want)
        assert (ex[0] > 100).all()


def test_sky_is_the_45_60_ring_only():
    m = master([(250, 250, 3.0)])
    # a level step on the 45-60 ring moves the excess; the same step at 31-44 px (between annulus and ring) does not
    stars, ex, _ = g.measure(m, flat_plate(m.shape, 250, 250, 0.0, ring_delta=0.01))
    want = expected_flat_excess(m, 0.0, ring_delta=0.01)
    assert np.allclose(ex[0], want, rtol=1e-6) and (ex[0] < 0).all(), (ex[0], want)
    plate = np.full(m.shape, SKY)
    rr = np.hypot(*np.mgrid[:SIZE, :SIZE] - np.array([250, 250])[:, None, None])
    plate[:, (rr >= 32) & (rr < 44)] += 0.05
    _, ex2, _ = g.measure(m, plate)
    assert np.allclose(ex2[0], 0.0, atol=1e-6), ex2[0]


def test_perfectly_cleaned_plate_has_zero_share():
    m = master([(150, 150, 3.0), (350, 350, 3.0), (150, 350, 3.0)])
    _, _, own = g.measure(m, m)
    thr = 0.1 * np.median(own, axis=0)
    assert (own.min(axis=0) > 0).all()
    stars, ex, _ = g.measure(m, np.full(m.shape, SKY))        # flat, noise-free sky plate
    assert len(stars) == 3
    # the plate's noise-free flat sky: excess is exactly zero in every channel, so no star is above a positive threshold
    assert np.allclose(ex, 0.0, atol=1e-9)
    assert not (ex > thr).any()


def test_threshold_mode_reads_the_inputs_own_halo():
    m = master([(150, 150, 3.0), (350, 350, 3.0)])
    with tempfile.TemporaryDirectory() as d:
        path = Path(d) / 'input.fits'
        fits.PrimaryHDU(m.astype(np.float32)).writeto(path)
        r = subprocess.run([sys.executable, '-I', str(HERE / 'glow_ring.py'), str(path), '--derive-threshold'],
                           capture_output=True, text=True)
        assert r.returncode == 0, r.stderr
        thr = [float(v) for v in re.search(r'glow threshold (\S+) (\S+) (\S+)', r.stdout).groups()]
        _, _, own = g.measure(g.three_planes(fits.getdata(path)), g.three_planes(fits.getdata(path)))
        assert np.allclose(thr, 0.1 * np.median(own, axis=0), rtol=1e-3)
        assert min(thr) > 0
        # and a plate that keeps the whole halo is all above it, a flat one none
        pl = Path(d) / 'keep.fits'
        fits.PrimaryHDU(m.astype(np.float32)).writeto(pl)
        t = ','.join(map(str, thr))
        r = subprocess.run([sys.executable, '-I', str(HERE / 'glow_ring.py'), str(path), str(pl), '--threshold', t],
                           capture_output=True, text=True)
        assert r.returncode == 0, r.stderr
        shares = [float(s) for s in re.findall(r'above ([\d.]+) %', r.stdout)]
        assert shares == [100.0, 100.0, 100.0], r.stdout
        assert len(re.findall(r'^glow [RGB] n 2 median ', r.stdout, re.M)) == 3


if __name__ == '__main__':
    for name, fn in sorted(globals().items()):
        if name.startswith('test_') and callable(fn):
            fn()
            print('ok', name)
