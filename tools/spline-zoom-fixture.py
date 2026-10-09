#!/usr/bin/env python
"""Regenerate src/TianWen.Lib.Tests/Data/spline-zoom-fixture.json.gz.

The oracle for TianWen.Lib.Imaging.SplineZoom, the C# port of
scipy.ndimage.zoom(plane, factor, order=3, mode='mirror'), every other argument at its default
(prefilter=True, grid_mode=False). Mirror is the default 'constant' mode to the bit (constant's
prefilter and edge taps ARE mirror), except on the shapes where the last output coordinate rounds
a hair past the input's last sample and 'constant' writes 0 over that whole row or column: a
rounding artefact that would draw a black line along the edge of a deconvolved image, so the
runtime does not reproduce it. Every case asserts the two modes agree, but for those two shapes,
where it asserts 'constant' zeroes the line and 'mirror' does not. The in-house deconvolver
(#844) runs its operator on a frame resampled UP by 1.28125 and resamples the result
back DOWN onto the native grid, and the training and every published readout did both
with that call (training/denoise/n2n_operator_master.py), so the C# runtime must give
the same numbers, not merely a similar cubic resample.

Each case is a float32 input plane, the output shape scipy produced and scipy's float32
output. Planes travel as base64 of little-endian float32 bytes, so every value reaches
the C# test exactly and the file stays small. The cases:

  - a structured random plane (37 x 53) up by 1.28125, as the runner zooms a frame;
  - scipy's own output of that, down by the tuple factor (h / hz, w / wz) that lands back
    on 37 x 53, as the runner does;
  - sharp features AT the edges and corners, up and down, which is where the boundary
    handling lives;
  - a downsample by 0.5, an upsample by 2, and tiny planes (1 x n, 1 x 1, 2 x 2, 3 x 3,
    and 3 x 3 down to 1 x 1);
  - values near 0 and 1 with a negative excursion, as a deconvolved output rings;
  - two shapes where scipy's last output row or column falls a rounding error past the
    input's last sample, where 'constant' writes cval (0) and 'mirror' reads the spline at
    the last sample, one of them the 3844 -> 3000 rows a 3000 px frame comes back down through;
  - an output the shape of the input, through a factor of 1 (scipy returns the input)
    and through 1.01 (it interpolates, at integer coordinates).

It also records scipy's output SIZE for a set of (size, factor) pairs, including a
product that lands exactly on a half (6000 * 1.28125 = 7687.5, which Python's round
takes to the even 7688).

Requires numpy and scipy. Run from the repo root:

    python tools/spline-zoom-fixture.py
"""

import base64
import gzip
import io
import json
import os

import numpy as np
import scipy
from scipy.ndimage import zoom

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                   "..", "src", "TianWen.Lib.Tests", "Data", "spline-zoom-fixture.json.gz")

UP = 1.28125


def b64(plane):
    return base64.b64encode(np.ascontiguousarray(plane, dtype="<f4").tobytes()).decode("ascii")


def structured(rng, h, w):
    """Smooth blobs, a gradient, stars and noise: something with every scale in it."""
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float64)
    p = 0.15 + 0.2 * (xx / w) + 0.1 * (yy / h)
    for _ in range(6):
        cy, cx = rng.uniform(0, h), rng.uniform(0, w)
        s = rng.uniform(2.0, 9.0)
        p += rng.uniform(0.05, 0.3) * np.exp(-((yy - cy) ** 2 + (xx - cx) ** 2) / (2 * s * s))
    for _ in range(8):
        cy, cx = rng.uniform(0, h), rng.uniform(0, w)
        s = rng.uniform(0.6, 1.4)
        p += rng.uniform(0.1, 0.6) * np.exp(-((yy - cy) ** 2 + (xx - cx) ** 2) / (2 * s * s))
    p += rng.normal(0, 0.01, (h, w))
    return p.astype(np.float32)


def edge_features(h, w):
    """Sharp features on the border rows and columns and in every corner."""
    p = np.full((h, w), 0.1, np.float64)
    p[0, 0] = 1.0
    p[0, w - 1] = 0.8
    p[h - 1, 0] = 0.6
    p[h - 2:, w - 2:] = 1.0
    p[0, 3:w // 2] = 0.9                      # a step along the top edge
    p[1::2, 0] = 0.0                          # Nyquist along the left edge
    p[h // 2, w - 1] = 1.0                    # a hot pixel on the right edge
    p[h - 1, w // 2] = 1.0                    # and one on the bottom edge
    return p.astype(np.float32)


def ringing(rng, h, w):
    """Near 0, near 1, and a negative excursion beside a hard edge, as a deconvolution rings."""
    p = rng.uniform(1e-6, 1e-3, (h, w))
    p[h // 3:2 * h // 3, w // 3:2 * w // 3] = 0.999 + rng.uniform(0, 1e-3, (2 * h // 3 - h // 3, 2 * w // 3 - w // 3))
    p[h // 3 - 1, w // 3:2 * w // 3] = -0.05
    p[h // 3:2 * h // 3, 2 * w // 3] = -0.03
    p[0, 0] = -0.02
    p[h - 1, w - 1] = 1.0
    return p.astype(np.float32)


def case(name, plane, factor, note, constant_zeroes_last_line=False):
    plane = np.asarray(plane, np.float32)
    out = zoom(plane, factor, order=3, mode="mirror")
    assert out.dtype == np.float32, out.dtype
    constant = zoom(plane, factor, order=3)
    if constant_zeroes_last_line:
        assert not np.array_equal(constant, out), f"{name}: expected 'constant' to zero a last line here"
    else:
        assert np.array_equal(constant, out), f"{name}: 'mirror' and 'constant' differ off the zeroed-line shapes"

    return {
        "name": name,
        "note": note,
        "factor": list(factor) if isinstance(factor, tuple) else [factor, factor],
        "inHeight": plane.shape[0],
        "inWidth": plane.shape[1],
        "input": b64(plane),
        "outHeight": out.shape[0],
        "outWidth": out.shape[1],
        "output": b64(out),
    }, out


def main():
    rng = np.random.default_rng(20261009)
    cases = []

    a = structured(rng, 37, 53)
    c, up = case("up-structured", a, UP, "37 x 53 up by 1.28125, as the runner zooms a frame")
    cases.append(c)
    h, w = a.shape
    hz, wz = up.shape
    c, _ = case("down-tuple-back", up, (h / hz, w / wz), "scipy's own up, down by (h / hz, w / wz) back onto 37 x 53")
    assert (c["outHeight"], c["outWidth"]) == (h, w)
    cases.append(c)

    e = edge_features(23, 29)
    c, eu = case("edges-up", e, UP, "sharp features on every edge and corner, up by 1.28125")
    cases.append(c)
    c, _ = case("edges-down-tuple", eu, (23 / eu.shape[0], 29 / eu.shape[1]), "the edge plane's up, back down")
    cases.append(c)
    c, _ = case("edges-down-0.61", e, 0.61, "the edge plane down by 0.61")
    cases.append(c)

    cases.append(case("down-half", structured(rng, 40, 26), 0.5, "40 x 26 down by 0.5")[0])
    cases.append(case("up-two", structured(rng, 9, 11), 2.0, "9 x 11 up by 2")[0])

    cases.append(case("tiny-1x7-up-two", structured(rng, 1, 7), 2.0, "one row: the row axis has n_in = 1")[0])
    cases.append(case("tiny-1x7-up", structured(rng, 1, 7), UP, "one row up by 1.28125: n_out = 1 on the row axis")[0])
    cases.append(case("tiny-1x1-up-three", np.array([[0.375]], np.float32), 3.0, "a single sample up by 3")[0])
    cases.append(case("tiny-2x2-up", structured(rng, 2, 2), UP, "2 x 2 up by 1.28125 (to 3 x 3)")[0])
    cases.append(case("tiny-2x2-up-two", structured(rng, 2, 2), 2.0, "2 x 2 up by 2")[0])
    cases.append(case("tiny-3x3-up", structured(rng, 3, 3), UP, "3 x 3 up by 1.28125 (to 4 x 4)")[0])
    cases.append(case("tiny-3x3-to-1x1", structured(rng, 3, 3), 0.3, "3 x 3 down to 1 x 1: n_out = 1 on both axes")[0])

    r = ringing(rng, 29, 33)
    c, ru = case("ringing-up", r, UP, "near 0, near 1 and negative, up by 1.28125")
    cases.append(c)
    cases.append(case("ringing-down-tuple", ru, (29 / ru.shape[0], 33 / ru.shape[1]), "its up, back down")[0])

    q = structured(rng, 22, 26)
    zq = zoom(q, (20 / 22, 12 / 26), order=3)
    assert np.all(zq[-1, :] == 0) and np.all(zq[:, -1] == 0), "expected 'constant' to write cval on the last row and column"
    c, qo = case("last-line-row-and-column", q, (20 / 22, 12 / 26),
                 "22 x 26 to 20 x 12: both axes' last coordinate rounds past n_in - 1, where 'constant' writes 0",
                 constant_zeroes_last_line=True)
    assert np.all(qo[-1, :] != 0) and np.all(qo[:, -1] != 0)
    cases.append(c)

    tall = np.full((3844, 1), 0.5, np.float32)
    assert zoom(tall, (3000 / 3844, 1.0), order=3)[-1, 0] == 0, "expected 'constant' to zero the last row"
    c, to = case("last-line-3844-to-3000", tall, (3000 / 3844, 1.0),
                 "3844 rows down to 3000, as a 3000 px frame comes back down: 'constant' zeroes the last row",
                 constant_zeroes_last_line=True)
    assert np.all(np.abs(to[:, 0] - 0.5) < 1e-6)
    cases.append(c)

    same = structured(rng, 5, 6)
    cases.append(case("same-shape-factor-one", same, 1.0, "factor 1: scipy returns the input")[0])
    cases.append(case("same-shape-factor-1.01", same, 1.01, "factor 1.01 keeps 5 x 6: scipy interpolates at integer coordinates")[0])

    sizes = []
    pairs = [(n, UP) for n in (1, 2, 3, 7, 23, 29, 37, 53, 3000, 4000, 4024, 6000, 6024)]
    pairs += [(47, 37 / 47), (68, 53 / 68), (5125, 4000 / 5125), (7688, 6000 / 7688), (3844, 3000 / 3844)]
    pairs += [(2, 1.25), (6, 1.25), (10, 0.25), (14, 0.25), (3, 0.3), (40, 0.5), (26, 0.5), (9, 2.0), (5, 1.01)]
    for n, f in pairs:
        s = zoom(np.zeros((n, 1), np.float32), (f, 1.0), order=0).shape[0]
        sizes.append({"n": n, "factor": f, "size": s})

    doc = {
        "version": 1,
        "scipy": scipy.__version__,
        "numpy": np.__version__,
        "call": "scipy.ndimage.zoom(plane, factor, order=3, mode='mirror'), float32 in and out",
        "cases": cases,
        "outputSizes": sizes,
    }
    raw = json.dumps(doc, indent=1).encode("ascii")
    buf = io.BytesIO()
    with gzip.GzipFile(filename="", mode="wb", fileobj=buf, mtime=0, compresslevel=9) as gz:
        gz.write(raw)
    with open(OUT, "wb") as f:
        f.write(buf.getvalue())
    print(f"wrote {os.path.normpath(OUT)}: {len(cases)} cases, {len(sizes)} sizes, "
          f"{len(raw)} bytes raw, {len(buf.getvalue())} gzipped")


if __name__ == "__main__":
    main()
