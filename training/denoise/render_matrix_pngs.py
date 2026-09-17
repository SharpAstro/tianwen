"""Re-render the matrix PNGs from the linear FITS with ONE linked stretch per variant and the display
boost applied in float before the 8-bit quantisation, so noise renders neutral and unquantised."""
import os
import sys

import numpy as np
from astropy.io import fits

sys.path.insert(0, "C:/Users/SebastianGodelet/source/repos/sharpastro/tianwen/training/denoise")
import n2n_operator as OP  # noqa: E402
from PIL import Image  # noqa: E402

ROOT = "C:/temp/e2/matrix"
SOFT = "C:/temp/e2/e210b-statue/soft/master_StatueofLibertyNebula_light_60s_-5C_g120.fits"
SHARP = "C:/temp/e2/e210b-statue/sharp/master_StatueofLibertyNebula_light_60s_-5C_g120.fits"
CONTRAST, BRIGHT = 1.35, 1.25


def rd(p, divisor=None):
    """The master in UNIT range: its own peak is the divisor when it is over 1 (the exporter's rule);
    an arm's output takes its input's divisor, since the operator wrote it back in the input's units."""
    with fits.open(p, memmap=False) as h:
        d = np.nan_to_num(np.asarray(next(x for x in h if x.data is not None).data, dtype=np.float32))
    if divisor is None:
        peak = float(d.max())
        divisor = peak if peak > 1.0 else 1.0
    return d / np.float32(divisor), divisor


def linked_params(d):
    """Background-neutralised linked stretch: each channel is shifted so its median sits on the lowest
    channel median (the viewer's background neutralisation), the black point is three MADs under that
    shared background, and ONE midtones balance serves all three channels, so the noise renders neutral."""
    meds = np.array([np.median(d[c]) for c in range(3)], dtype=np.float32)
    lum = d.mean(axis=0)
    mad = float(1.4826 * np.median(np.abs(lum - np.median(lum))))
    # Black six MADs under the background and the background at 0.25: half the auto-stretch's slope,
    # so the sky's noise is visible but not the picture.
    black = float(meds.min()) - 6.0 * mad
    mins = (meds - meds.min()) + np.float32(black)   # subtracting mins[c] equalises the medians and sets the black point
    return mins, OP.midtones_balance_for(6.0 * mad)


def render(d, mins, beta, path):
    out = np.empty_like(d)
    for c in range(3):
        y = OP.mtf(beta, np.clip(d[c] - mins[c], 0.0, None).astype(np.float64))
        out[c] = ((y - 0.5) * CONTRAST + 0.5) * BRIGHT
    rgb = (np.clip(out, 0.0, 1.0) * 255.0 + 0.5).astype(np.uint8).transpose(1, 2, 0)
    Image.fromarray(rgb, "RGB").save(path, optimize=True)


SHARP = f"{ROOT}/sharp_flat.fits"   # the reference on the same flattened footing as every row
VARIANTS = {"flat": f"{ROOT}/flat.fits", "flat_n2n": f"{ROOT}/flat_n2n.fits"}
for v, inp in VARIANTS.items():
    d, divisor = rd(inp)
    mins, beta = linked_params(d)  # the variant's own linked stretch, shared by its three arms
    print(f"{v}: divisor {divisor:.4g}, linked beta {beta:.4f}, mins {mins}")
    render(d, mins, beta, f"{ROOT}/{v}/input.png")
    for arm in ("e30", "prior"):
        render(rd(f"{ROOT}/{v}/{arm}.fits", divisor)[0], mins, beta, f"{ROOT}/{v}/{arm}.png")
d, divisor = rd(SHARP)
mins, beta = linked_params(d)
print(f"sharp: divisor {divisor:.4g}, linked beta {beta:.4f}")
render(d, mins, beta, f"{ROOT}/sharp/input.png")
