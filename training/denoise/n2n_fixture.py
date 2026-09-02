r"""Generate the cross-language parity fixture for TianWen's N2nDenoiser.

The C# side reproduces this exact plate and must land on this exact answer. What that pins is
the whole deployment path, not the graph: NCHW packing, the median-fill border, the 256 px
chunking, the stitch that drops a 16 px rim, and the blend. The graph itself is already pinned
by `n2n_export.py` (max |diff| 1.49e-7 against torch).

TWO CHOICES HERE ARE DELIBERATE.

**The plate is generated, not shipped.** An input fixture of 224x224x3 float32 is 600 KiB of
incompressible noise, and it would have to be read identically by both languages to be worth
anything. Instead both sides build the plate from the same explicit LCG. `numpy.random` and
`System.Random` do not agree on anything, so the generator is stated in integer arithmetic that
does: `s = (s * 1664525 + 1013904223) mod 2^32`, exactly as written, in both files.

**160 x 160 is chosen so the whole image is ONE chunk**, which makes a failure attributable:
with one chunk there is no overlap averaging to hide behind. The size is not obvious and 224 is
the trap. `Split` steps by `chunkSize - overlap` = 192 and its loop runs while `i < height`, so a
224 source (padded to 256) yields FOUR chunks -- one full tile plus three 64 px slivers, which
are replicate-padded back up to 256 and do contribute to the output. 160 pads to 192, 192 is not
less than 192, and the loop stops after one.

That single chunk is then replicate-padded 192 -> 256 by the runner, so this fixture also covers
the edge-chunk path that every real image takes at its right and bottom margins.

The fixture stores per-channel statistics plus a lattice of sampled pixels rather than the whole
raster. That is not a shortcut -- it is what makes the test diagnostic. A transposed tensor moves
the lattice, a broken border moves the corners and not the centre, and a wrong blend moves the
mean. A raster diff would say only that something is wrong.

Usage:  python n2n_fixture.py
        (writes straight into the C# test's embedded resource; --out to put it elsewhere, --cache
        and --ckpt to fixture a different checkpoint)
"""
import argparse
import io
import json
import os
import sys

import numpy as np

import n2n_smoke as S
from n2n_paths import cache

# The fixture IS the C# test's embedded resource, so it is written there rather than copied by hand.
REPO_FIXTURE = os.path.normpath(os.path.join(
    os.path.dirname(os.path.abspath(__file__)),
    "..", "..", "src", "TianWen.Lib.Tests", "Data", "n2n-parity-fixture.json"))

SIZE = 160           # -> 192 after the 16 px border, and Split yields exactly one chunk
TILE = 256           # the model's declared tile; the chunk is replicate-padded up to it
CH = 3
# The sky level a real master sits at. NOT arbitrary: the net carries a learned level prior from
# its eight training sessions and drags an input toward it, so a plate at 0.15 comes out shifted
# by +0.03 and would bake that artefact into the fixture as if it were the expected answer.
BACKGROUND = 0.26
NOISE_AMPLITUDE = 0.04
STAR_COUNT = 40
LATTICE = 8          # 8x8 sample points per channel, corners included


class Lcg:
    """Numerical Recipes' LCG, stated so C# can restate it. Deliberately not a library RNG."""

    def __init__(self, seed):
        self.s = seed & 0xFFFFFFFF

    def next_unit(self):
        self.s = (self.s * 1664525 + 1013904223) & 0xFFFFFFFF
        return self.s / 4294967296.0


def build_plate():
    """Flat sky + Gaussian stars + per-pixel grain. Astro-shaped on purpose: the model is
    conditioned on the background sigma of what it is handed, so a plate with no faint end
    would exercise the conditioning at a value no real frame produces."""
    rng = Lcg(20260817)
    planes = np.full((CH, SIZE, SIZE), BACKGROUND, dtype=np.float64)

    for _ in range(STAR_COUNT):
        cx = rng.next_unit() * SIZE
        cy = rng.next_unit() * SIZE
        amp = 0.05 + 0.60 * rng.next_unit()
        sigma = 1.2 + 1.8 * rng.next_unit()
        x0, x1 = max(0, int(cx - 8)), min(SIZE, int(cx + 9))
        y0, y1 = max(0, int(cy - 8)), min(SIZE, int(cy + 9))
        ys = np.arange(y0, y1)[:, None]
        xs = np.arange(x0, x1)[None, :]
        g = amp * np.exp(-(((xs - cx) ** 2 + (ys - cy) ** 2) / (2.0 * sigma * sigma)))
        for c in range(CH):
            planes[c, y0:y1, x0:x1] += g * (0.7 + 0.3 * ((c + 1) / CH))

    for c in range(CH):
        for y in range(SIZE):
            for x in range(SIZE):
                planes[c, y, x] += NOISE_AMPLITUDE * (rng.next_unit() - 0.5)

    plate = np.clip(planes, 0.0, 1.0).astype(np.float32)
    # One dead pixel per channel, after the grain so the LCG stream is untouched. Since 2026-09-02
    # the C# runner applies the exporter's ApplyInputStretch (the domain every training tile was
    # stored in) and its auto-detect keys on median MINUS min: a plate at 0.26 with +-0.02 grain has
    # a min near 0.24, would be read as linear and stretched, and torch's answer for the raw plate
    # would stop applying. The dead pixel puts the plate in band the way a training tile is (min 0,
    # median near 0.25), so the runner feeds it as it is. Restated in N2nDenoiserTests.BuildPlate.
    plate[:, 0, 0] = 0.0
    return plate


def add_border(plane, border):
    """`ChunkedInference.AddBorder`: median fill, per plane. numpy's median and
    `StatisticsHelper.MedianFast` agree on the even-length convention (mean of the two middle
    values), which is the only place these two could quietly differ."""
    h, w = plane.shape
    out = np.full((h + 2 * border, w + 2 * border), np.median(plane), dtype=np.float32)
    out[border:border + h, border:border + w] = plane
    return out


def replicate_pad(plane, size):
    """The runner's edge-chunk padding: replicate the rightmost column, then the bottom row.

    Never zero-padded -- a hard edge is structure, and a net whose job is to preserve structure
    will faithfully preserve it into the region the stitch keeps.
    """
    h, w = plane.shape
    out = np.empty((size, size), dtype=np.float32)
    out[:h, :w] = plane
    if size > w:
        out[:h, w:] = plane[:, w - 1:w]
    if size > h:
        out[h:, :] = out[h - 1:h, :]
    return out


def main():
    import torch

    p = argparse.ArgumentParser()
    p.add_argument("--cache", default=cache("n2n-d8"), help="the prepared cache holding --ckpt")
    p.add_argument("--ckpt", default="n2n_v19d_s2_final.pt")
    p.add_argument("--out", default=REPO_FIXTURE,
                   help="where the fixture goes; the default is the C# test's embedded resource")
    a = p.parse_args()

    model, planes_cond = S.load_model(a.cache, a.ckpt, "cpu")
    model.eval()

    src = build_plate()
    border = S.BORDER
    chunk = np.stack([add_border(src[c], border) for c in range(CH)])
    chunk_size = SIZE + 2 * border
    tile = np.stack([replicate_pad(chunk[c], TILE) for c in range(CH)])
    print(f"plate {src.shape} -> chunk {chunk.shape} -> model tile {tile.shape}")

    with torch.no_grad():
        x = torch.from_numpy(tile[None])
        xc = S.with_sigma(x, strength=1.0, planes=planes_cond)
        den_tile = model(xc).numpy()[0]

    # Crop back to the chunk, then the per-channel level restore the runner applies before
    # stitching (N2nLinearRunner.RestoreLevel). Its input median is the BORDERED chunk's, not the
    # bare plate's, because that is the array the runner holds at that point.
    den_chunk = den_tile[:, :chunk_size, :chunk_size].copy()
    for c in range(CH):
        den_chunk[c] += np.median(chunk[c]) - np.median(den_chunk[c])

    den = den_chunk[:, border:border + SIZE, border:border + SIZE]

    step = SIZE // (LATTICE - 1) if LATTICE > 1 else SIZE
    coords = [min(i * step, SIZE - 1) for i in range(LATTICE)]

    fixture = {
        "note": "Generated by n2n_fixture.py. Regenerate if the checkpoint or the plate changes.",
        "checkpoint": a.ckpt,
        "model_file": "tianwen_denoise_osc_v19d.onnx",
        "size": SIZE, "tile": TILE, "channels": CH, "border": border,
        "background": BACKGROUND, "noise_amplitude": NOISE_AMPLITUDE,
        "star_count": STAR_COUNT, "seed": 20260817, "lattice": LATTICE,
        "input": {"mean": [], "std": []},
        "output": {"mean": [], "std": []},
        "samples": [],
    }
    for c in range(CH):
        fixture["input"]["mean"].append(float(src[c].mean()))
        fixture["input"]["std"].append(float(src[c].std()))
        fixture["output"]["mean"].append(float(den[c].mean()))
        fixture["output"]["std"].append(float(den[c].std()))
        print(f"  ch{c}: in mean {src[c].mean():.6f} std {src[c].std():.6f}  ->  "
              f"out mean {den[c].mean():.6f} std {den[c].std():.6f}  "
              f"(noise x{den[c].std()/src[c].std():.3f})")

    for c in range(CH):
        for y in coords:
            for x in coords:
                fixture["samples"].append(
                    {"c": c, "x": int(x), "y": int(y),
                     "in": float(src[c, y, x]), "out": float(den[c, y, x])})

    # newline="\n": the repo stores JSON with LF, and text mode on Windows would write CRLF.
    with io.open(a.out, "w", encoding="utf-8", newline="\n") as f:
        json.dump(fixture, f, indent=1)
    print(f"\nwritten {a.out} ({len(fixture['samples'])} samples)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
