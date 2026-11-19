"""Pins nebula_rows.py on a synthetic master: no GPU, no owner data. Run with `python -I test_nebula_rows.py`; exit 0 is a pass."""
import re
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))        # -I leaves the script's folder off sys.path
import numpy as np
from astropy.io import fits
from scipy.ndimage import gaussian_filter

import nebula_rows as n

SKY, NOISE, SIZE, CENTRE = 0.02, 0.002, 700, 350
# 40 stars on the nebula, 60 to 3000 pixel sigmas high, a grid of 8 by 5.
STARS = [(270 + 40 * j, 210 + 40 * i, 60 * (3000 / 60) ** ((8 * j + i) / 39)) for j in range(5) for i in range(8)]


def blob(y, x, height, width):
    yy, xx = np.mgrid[:SIZE, :SIZE]
    return height * np.exp(-((yy - y) ** 2 + (xx - x) ** 2) / (2.0 * width * width))


def scene():
    """The nebula alone (sky, noise and a broad glow 100 sigmas high) and the same with the stars on it."""
    rng = np.random.default_rng(3)
    nebula = SKY + NOISE * rng.standard_normal((SIZE, SIZE)) + blob(CENTRE, CENTRE, 100 * NOISE, 120)
    stars = sum(blob(y, x, peak * NOISE, 1.3) for y, x, peak in STARS)
    return nebula, nebula + stars


def measured(master, starless):
    with tempfile.TemporaryDirectory() as d:
        a, b = Path(d) / "master.fits", Path(d) / "starless.fits"
        fits.PrimaryHDU(master.astype(np.float32)).writeto(a); fits.PrimaryHDU(starless.astype(np.float32)).writeto(b)
        return n.rows(str(a), str(b))


def test_the_bar_is_what_its_comment_says():
    noise = np.random.default_rng(1).standard_normal((1024, 1024))
    band = gaussian_filter(noise, 1) - gaussian_filter(noise, 4)
    assert abs(band.std() - n.BAND_NOISE) < 0.004, band.std()
    assert n.STRONG == 50.0 * 0.28                      # the bar every figure so far was made with
    assert 54 < n.STRONG / n.BAND_NOISE < 55


def test_a_clean_removal_adds_nothing_and_leaves_no_peak():
    nebula, master = scene()
    r = measured(master, nebula)
    assert r["nebula_px"] > 100_000, r
    assert r["peaks"] == len(STARS), r                  # every star is a peak over the bar, and no noise is
    assert r["top"] == 4 and r["top_standing"] == 0 and r["standing"] == 0.0 and r["standing_top"] == 0.0, r
    assert r["added"] == 0.0 and r["added_frame"] == 0.0, r
    # Broad is not zero for a clean removal: a 9 px median kills a faint star and keeps the skirt of a bright one (2 % of
    # the peak of a star 1.3 px wide), so the stars rightly taken give the figure a floor. Here 40 stars up to 3000 sigma.
    assert 0.05 < r["broad"] < 0.12, r
    faint = [s for s in STARS if s[2] < 100]
    only_faint = nebula + sum(blob(y, x, peak * NOISE, 1.3) for y, x, peak in faint)
    assert measured(only_faint, nebula)["broad"] < 0.002


def test_added_is_three_sigma_of_a_three_pixel_mean():
    nebula, master = scene()
    patch = np.zeros((SIZE, SIZE)); patch[320:340, 300:320] = 1.0       # between four stars, clear of each by 10 px
    # A 3 px mean of noise sigma has a third of it, so the bar is one pixel sigma of the mean: 1.2 is over, 0.8 is not.
    over = measured(master, nebula + 1.2 * NOISE * patch)
    under = measured(master, nebula + 0.8 * NOISE * patch)
    assert under["added"] == 0.0, under
    # The 3 px mean reaches the full step only a pixel inside the patch's edge: 18 x 18 of its 20 x 20.
    assert over["added"] == 100.0 * 18 * 18 / over["nebula_px"], (over["added"], over["nebula_px"])


def test_a_peak_stands_when_it_keeps_over_half_its_height():
    nebula, master = scene()
    brightest = sorted(STARS, key=lambda s: s[2])[-4:]                  # the brightest tenth of 40
    whole = measured(master, nebula + sum(blob(y, x, peak * NOISE, 1.3) for y, x, peak in brightest))
    assert whole["top"] == 4 and whole["top_standing"] == 4 and whole["standing_top"] == 100.0, whole
    assert whole["standing"] == 100.0 * 4 / len(STARS), whole
    most = measured(master, nebula + sum(blob(y, x, 0.6 * peak * NOISE, 1.3) for y, x, peak in brightest))
    less = measured(master, nebula + sum(blob(y, x, 0.4 * peak * NOISE, 1.3) for y, x, peak in brightest))
    assert most["top_standing"] == 4 and less["top_standing"] == 0, (most, less)


def test_broad_reads_a_hole_and_not_a_star():
    nebula, _ = scene()
    hole = blob(330, 330, 3 * NOISE, 12)
    # No star here, so the figure is the hole's alone. The 9 px median keeps a hole 12 px wide whole; what exceeds a
    # third of a sigma is averaged over the nebula in sigma.
    r = measured(nebula, nebula - hole)
    want = np.clip(hole - NOISE / 3, 0, None).sum() / (r["nebula_px"] * NOISE)
    assert abs(r["broad"] - want) < 0.15 * want, (r["broad"], want)
    assert measured(nebula, nebula)["broad"] == 0.0


def test_the_command_line():
    nebula, master = scene()
    with tempfile.TemporaryDirectory() as d:
        a, b = Path(d) / "master.fits", Path(d) / "starless.fits"
        fits.PrimaryHDU(master.astype(np.float32)).writeto(a); fits.PrimaryHDU(nebula.astype(np.float32)).writeto(b)
        r = subprocess.run([sys.executable, "-I", str(HERE / "nebula_rows.py"), str(a), str(b), "--name", "Syn"], capture_output=True, text=True)
        assert r.returncode == 0, r.stderr
        assert re.search(r"^short: Syn neb added 0\.0% broad 0\.\d\d peaks standing 0\.0/0\.0 \(0 of 4\)$", r.stdout, re.M), r.stdout
        assert "compact peaks of the input over 54 sigma of their band" in r.stdout, r.stdout
        # --dir with a scorecard that is not there is an error that says so, not a TypeError.
        r = subprocess.run([sys.executable, "-I", str(HERE / "nebula_rows.py"), "--dir", d, "--config", str(Path(d) / "none.json")], capture_output=True, text=True)
        assert r.returncode == 2 and "does not exist" in r.stderr, (r.returncode, r.stderr)


if __name__ == "__main__":
    for name, fn in sorted(globals().items()):
        if name.startswith("test_") and callable(fn):
            fn()
            print("ok", name)
