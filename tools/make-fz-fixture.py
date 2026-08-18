#!/usr/bin/env python
"""Regenerate src/TianWen.Lib.Tests/Data/tilecompressed.fz.

A miniature of what Siril / fpack actually emit: an EMPTY primary HDU followed by
the image as a tile-compressed binary table extension. That layout is the point of
the fixture -- it is what a reader assuming the image lives in HDU 0 gets wrong,
and fpack has no choice about it, since a binary table cannot be a primary HDU.

Kept to 3x24x32 float32 so the fixture is a few KB; the shape mirrors a real OSC
stack (3 planes, quantized floats, RICE_1 + subtractive dither 2) and the header
carries the cards TianWen's reader actually parses.

Requires: astropy (pip install astropy). Run from the repo root:

    python tools/make-fz-fixture.py
"""

import os

import numpy as np
from astropy.io import fits

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                   "..", "src", "TianWen.Lib.Tests", "Data", "tilecompressed.fz")

SUBTRACTIVE_DITHER_2 = 2


def main():
    rng = np.random.default_rng(20260818)

    # A gradient plus noise: smooth data would compress so well that cfitsio would
    # take the gzip fallback instead of the quantize + Rice path we want exercised.
    idx = np.indices((3, 24, 32)).astype(np.float64)
    data = (0.5 + 1e-3 * (idx[1] + 2 * idx[2]) + rng.normal(0, 3e-3, (3, 24, 32)))
    data = data.astype(np.float32)

    hdr = fits.Header()
    hdr["OBJECT"] = ("Bubble Nebula", "Name of the object of interest")
    hdr["DATE-OBS"] = ("2026-08-13T02:52:12.152468", "observation start")
    hdr["EXPTIME"] = (150.0, "[s]  Exposure time duration")
    hdr["INSTRUME"] = "AA585CTEC"
    hdr["TELESCOP"] = "200P"
    hdr["FOCALLEN"] = (1180.0, "[mm]  Focal length")
    hdr["XPIXSZ"] = 2.9
    hdr["YPIXSZ"] = 2.9
    hdr["CCD-TEMP"] = -9.9
    hdr["SET-TEMP"] = -10.0
    hdr["GAIN"] = 150
    hdr["OFFSET"] = 500
    hdr["ROWORDER"] = ("TOP-DOWN", "Order of the rows in image array")
    hdr["IMAGETYP"] = "LIGHT"
    hdr["STACKCNT"] = (163, "Stack frames")
    hdr["SITELAT"] = 53.0666666666667
    hdr["SITELONG"] = -2.96666666666667
    # A WCS, so the reader's plate solution survives the header translation too.
    hdr["CTYPE1"] = "RA---TAN"
    hdr["CTYPE2"] = "DEC--TAN"
    hdr["CRPIX1"] = 16.0
    hdr["CRPIX2"] = 12.0
    hdr["CRVAL1"] = 350.185220211384
    hdr["CRVAL2"] = 61.1914981059903
    hdr["CD1_1"] = -1.4e-4
    hdr["CD1_2"] = 0.0
    hdr["CD2_1"] = 0.0
    hdr["CD2_2"] = 1.4e-4

    if os.path.exists(OUT):
        os.remove(OUT)

    hdu = fits.CompImageHDU(data=data, header=hdr, compression_type="RICE_1",
                            tile_shape=(1, 1, 32), quantize_level=16,
                            quantize_method=SUBTRACTIVE_DITHER_2)
    fits.HDUList([fits.PrimaryHDU(), hdu]).writeto(OUT)

    with fits.open(OUT) as hdul:
        assert hdul[0].data is None, "the primary HDU must stay empty"
        assert hdul[1].data.shape == (3, 24, 32)
    print(f"{OUT} -> {os.path.getsize(OUT)} bytes")


if __name__ == "__main__":
    main()
