"""Pins teacher_combine.py's rule on a synthetic master: no GPU, no owner data.
Run with `python -I test_teacher_combine.py`; exit 0 is a pass.

The scene: dark sky with a plateau of bright nebula (100 sky sigma). Nine members (five "any", a pair, two trusted),
each the master less the features it takes. What the rule must do with each feature:

  S1  a star on dark sky one "any" member takes            taken under --off med and low; kept under --off med2
  S6  a star on dark sky two "any" members take            taken under every --off rule
  S2  a star on the nebula one "any" member takes          kept: on bright nebula one such member is not enough
  S3  a star on the nebula one trusted member takes        taken
  S4  a star on the nebula both members of the pair take   taken
  S5  a star on the nebula one member of the pair takes    kept
  B1  a broad blob on the nebula two members take          kept: the median plate keeps what a minority takes
  B2  a broad blob on dark sky one member takes            kept under --off med and med2, taken under --off low
  K   a knot on the nebula seven of the nine take, with a star at its centre only a trusted member takes
                                                           the knot goes as the median plate has it, and nothing is
                                                           over-subtracted: the case the first construction failed

And for every pixel: the output is never under the lowest member's plate nor over the members' median plate.
"""
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))        # -I leaves the script's folder off sys.path
import numpy as np

from teacher_combine import combine

SIZE, SKY, NOISE = 320, 0.01, 1e-4
STAR, BLOB, KNOT = 20.0, 10.0, 30.0           # peak heights in sky sigma
N_ANY, PAIR, TRUSTED = 5, {5, 6}, {7, 8}


def gauss(x, y, peak, width):
    yy, xx = np.mgrid[:SIZE, :SIZE]
    return peak * NOISE * np.exp(-((yy - y) ** 2 + (xx - x) ** 2) / (2.0 * width * width))


def scene():
    rng = np.random.default_rng(7)
    master = np.full((SIZE, SIZE), SKY, np.float64)
    master[40:160, 40:160] += 100.0 * NOISE                      # the bright nebula
    feats = {
        "S1": (gauss(250, 60, STAR, 1.2), {0}),
        "S6": (gauss(250, 120, STAR, 1.2), {3, 4}),
        "S2": (gauss(70, 70, STAR, 1.2), {1}),
        "S3": (gauss(100, 70, STAR, 1.2), {7}),
        "S4": (gauss(130, 70, STAR, 1.2), {5, 6}),
        "S5": (gauss(70, 100, STAR, 1.2), {5}),
        "B1": (gauss(125, 130, BLOB, 8.0), {0, 1}),
        "B2": (gauss(250, 250, BLOB, 8.0), {2}),
        "K": (gauss(90, 125, KNOT, 5.0), {0, 1, 2, 3, 4, 5, 6}),
        "Kstar": (gauss(90, 125, STAR, 1.2), {7}),
    }
    for light, _ in feats.values():
        master += light
    master += rng.normal(0.0, NOISE, master.shape)
    plates = []
    for i in range(9):
        p = master.copy()
        for light, takers in feats.values():
            if i in takers:
                p -= light
        plates.append((p + rng.normal(0.0, 1e-7, p.shape)).astype(np.float32))
    return master.astype(np.float32), plates, feats


def taken(master, out, light, half):
    """The share of a feature's light the output took, over the box where the feature stands."""
    y, x = np.unravel_index(np.argmax(light), light.shape)
    box = (slice(y - half, y + half + 1), slice(x - half, x + half + 1))
    return float((master - out)[box].sum() / light[box].sum())


def main():
    master, plates, feats = scene()
    low = np.min(np.stack(plates), axis=0)
    med = np.median(np.stack(plates), axis=0)
    results = {}
    for off in ("med", "med2", "low"):
        out, weight, share = combine(master, plates, PAIR, TRUSTED, off)
        assert 0.10 < share < 0.20, f"the nebula's share of the frame reads {share:.3f}"
        assert weight[100, 100] > 0.99 and weight[250, 250] < 0.01, "the nebula weight is not 1 inside and 0 outside"
        assert float((out - low).min()) > -1e-6, f"--off {off}: a pixel stands {float((out - low).min()):.2e} under the lowest plate"
        assert float((out - med).max()) < 1e-6, f"--off {off}: a pixel stands {float((out - med).max()):.2e} over the median plate"
        results[off] = {k: taken(master, out, v[0], 12 if k in ("B1", "B2", "K") else 3) for k, v in feats.items()}
    for off, r in results.items():
        for k in ("S3", "S4", "S6") + (() if off == "med2" else ("S1",)):
            assert r[k] > 0.9, f"--off {off}: {k} taken only {r[k]:.2f}"
        for k in ("S2", "S5", "B1") + (("S1",) if off == "med2" else ()):
            assert r[k] < 0.1, f"--off {off}: {k} taken {r[k]:.2f}, it should stay"
    for off in ("med", "med2"):
        assert results[off]["B2"] < 0.25, f"--off {off}: the dark-sky blob one member took is taken {results[off]['B2']:.2f}"
    assert results["low"]["B2"] > 0.95, f"--off low: the dark-sky blob one member took is taken only {results['low']['B2']:.2f}"
    # The knot: the median plate has it out; the star at its centre that only a trusted member takes adds nothing
    # there, since that member's plate still holds the knot and stands over the median plate.
    out, _, _ = combine(master, plates, PAIR, TRUSTED, "med")
    knot = feats["K"][0]
    y, x = np.unravel_index(np.argmax(knot), knot.shape)
    box = (slice(y - 12, y + 13), slice(x - 12, x + 13))
    assert np.abs(out[box] - med[box]).max() < 1e-6, "at the knot the output is not the median plate"

    # Absent pixels and identical members.
    holed = [p.copy() for p in plates]
    for p in holed:
        p[:4, :4] = np.nan
    out, _, _ = combine(master, holed, PAIR, TRUSTED, "med")
    assert np.isnan(out[:4, :4]).all() and np.isfinite(out[4:, 4:]).all(), "an absent pixel did not stay absent"
    # An absent ring (zero in the master, NaN in the plates, as remove-stars writes them) of 12 percent or more of the
    # frame must not move the sky: the nebula share stays what it was over the live frame, and S6 is still taken.
    for ring in (24, 40):
        padded = np.zeros((SIZE + 2 * ring,) * 2, np.float32)
        padded[ring:-ring, ring:-ring] = master
        ring_plates = []
        for p in plates:
            q = np.full(padded.shape, np.nan, np.float32)
            q[ring:-ring, ring:-ring] = p
            ring_plates.append(q)
        live_share = (SIZE / (SIZE + 2 * ring)) ** 2
        assert live_share < 0.88, "the ring is under 12 percent of the frame"
        for off in ("med", "med2"):
            out, _, share = combine(padded, ring_plates, PAIR, TRUSTED, off)
            reading = share / live_share
            assert abs(reading - 0.128) < 0.03, f"--off {off}, ring {ring} px: the nebula reads {reading:.3f} of the live frame"
            inner = out[ring:-ring, ring:-ring]
            s6 = taken(master, inner, feats["S6"][0], 3)
            assert s6 > 0.9, f"--off {off}, ring {ring} px: S6 taken only {s6:.2f}"
    same = [plates[0]] * 9
    out, _, _ = combine(master, same, PAIR, TRUSTED, "med")
    assert np.abs(out - plates[0]).max() < 1e-7, "nine identical members did not give their own plate"   # the blend's rounding
    colour = np.stack([master, master, master])
    out, _, _ = combine(colour, [np.stack([p, p, p]) for p in plates], PAIR, TRUSTED, "low")
    assert out.shape == colour.shape, "a three-channel master did not give a three-channel plate"
    print("teacher_combine: every case holds", {k: round(v, 2) for k, v in results["med"].items() if k != "Kstar"})


if __name__ == "__main__":
    main()
