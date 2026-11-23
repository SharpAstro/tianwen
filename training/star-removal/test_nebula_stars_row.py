"""Pins nebula_stars_row.py's magnitude check on a synthetic master: no GPU, no owner data.
Run with `python -I test_nebula_stars_row.py`; exit 0 is a pass.

The scene: dark sky with 260 stars whose light matches their magnitude, and a plateau of bright nebula (60 sky sigma) that
holds six real stars and six bumps of the nebula, each bump at the place of a catalogued star a hundred times too faint to
be it. The plate takes every star and keeps the bumps, as a remover should. Without the check the row counts the bumps as
stars left; with it they are no stars.
"""
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))        # -I leaves the script's folder off sys.path
import numpy as np
from astropy.io import fits

from nebula_stars_parse import parse_row

SIZE, SKY, NOISE, ZERO = 640, 0.01, 1e-4, 18.0
IN_DISC = 1.0 - np.exp(-2.0)         # a Gaussian of sigma 1 px holds this share of its light inside 2 px
DARK = [(40 + 28 * j, 40 + 28 * i, 40.0 * 3.0 ** ((13 * i + j) / 259.0)) for i in range(20) for j in range(13)]
REAL = [(440, 440), (480, 440), (520, 440), (560, 440), (440, 500), (480, 500)]
BUMPS = [(520, 500), (560, 500), (440, 560), (480, 560), (520, 560), (560, 560)]
LIGHT = 60.0


def blob(x, y, light, width):
    """A Gaussian whose light inside 2 px is `light` sky sigma when its sigma is 1 px."""
    yy, xx = np.mgrid[:SIZE, :SIZE]
    g = np.exp(-((yy - y) ** 2 + (xx - x) ** 2) / (2.0 * width * width))
    return light * NOISE / IN_DISC * g / (2.0 * np.pi * width * width)


def mag(light):
    return ZERO - 2.5 * np.log10(light)


def scene():
    rng = np.random.default_rng(5)
    ground = SKY + NOISE * rng.standard_normal((SIZE, SIZE))
    ground[416:608, 416:608] += 60 * NOISE                                   # six by six blocks of bright nebula
    ground += sum(blob(x, y, LIGHT, 1.2) for x, y in BUMPS)                  # the nebula's own bumps: in master and plate
    stars = sum(blob(x, y, light, 1.0) for x, y, light in DARK) + sum(blob(x, y, LIGHT, 1.0) for x, y in REAL)
    rows = [(x, y, mag(light)) for x, y, light in DARK] + [(x, y, mag(LIGHT)) for x, y in REAL] \
        + [(x, y, mag(LIGHT / 100.0)) for x, y in BUMPS]
    return ground + stars, ground, rows


def row(folder, rows, *extra):
    cat = folder / f"gaia{len(rows)}.csv"
    cat.write_text("x,y,mag\n" + "".join(f"{x},{y},{m:.3f}\n" for x, y, m in rows), encoding="utf-8")
    r = subprocess.run([sys.executable, "-I", str(HERE / "nebula_stars_row.py"), str(folder / "master.fits"), str(cat),
                        str(folder / "plate.fits"), "--name", "T", *extra], capture_output=True, text=True, check=True)
    return parse_row(r.stdout.strip().splitlines()[-1], "T"), r.stderr


with tempfile.TemporaryDirectory() as d:
    folder = Path(d)
    master, plate, rows = scene()
    fits.PrimaryHDU(master.astype(np.float32)).writeto(folder / "master.fits")
    fits.PrimaryHDU(plate.astype(np.float32)).writeto(folder / "plate.fits")

    checked, note = row(folder, rows)
    assert checked["bright_n"] == 6 and checked["bright_left"] == "0.0", checked
    assert abs(float(checked["bright_taken"]) - 1.0) < 0.1, checked
    assert checked["dark_n"] == 260 and checked["dark_left"] == "0.0", checked
    assert "keeps 6 of 12 on bright nebula" in note, note

    # The row as it read on 2026-11-22: the six bumps count as stars the plate left.
    plain, _ = row(folder, rows, "--match", "0")
    assert plain["bright_n"] == 12 and plain["bright_left"] == "50.0", plain

    # Too few dark-sky stars to fit a zero point on: the check is off, and the script says so.
    few, note = row(folder, rows[:40] + rows[260:])
    assert few["bright_n"] == 12 and "the magnitude check is off" in note, (few, note)
print("ok")
