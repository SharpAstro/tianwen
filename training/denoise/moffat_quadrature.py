"""Does a quadrature of FWHMs over-read a Moffat difference kernel, and by how much?

E1b measured the estimated difference width at 1.23 to 1.25 times the injected one at 1.3-1.6x blur,
0.92 to 0.94 at 1.1-1.3x, 1.16 at 1.6-2.0x and 1.11 at 2-3x, and the plan's first guess was that
sqrt(obs^2 - clean^2) is what quadrature does to Moffat profiles. This computes, with no star
detection and no noise, the FWHM of clean (x) kernel for Moffat profiles in the archive's range, and the
quadrature estimate's ratio to the true kernel width. If the ratios here are near 1.0 the bias is not
composition arithmetic and comes from the MEASUREMENT on the blurred frame instead.
"""
import numpy as np

N = 1024          # samples across the grid
DX = 0.05         # px per sample (fine enough for a 1 px kernel core)


def moffat(fwhm, beta, r):
    alpha = fwhm / (2.0 * np.sqrt(2.0 ** (1.0 / beta) - 1.0))
    return (1.0 + (r / alpha) ** 2) ** (-beta)


def grid_profile(fwhm, beta):
    ax = (np.arange(N) - N / 2) * DX
    xx, yy = np.meshgrid(ax, ax)
    r = np.sqrt(xx * xx + yy * yy)
    return moffat(fwhm, beta, r)


def fwhm_of(img):
    """Half-maximum crossing of the radial profile through the centre, doubled; interpolated."""
    c = N // 2
    row = img[c, c:]
    row = row / row[0]
    i = np.argmax(row < 0.5)
    t = (row[i - 1] - 0.5) / (row[i - 1] - row[i])
    return 2.0 * (i - 1 + t) * DX


def convolve(a, b):
    fa = np.fft.rfft2(np.fft.ifftshift(a))
    fb = np.fft.rfft2(np.fft.ifftshift(b))
    return np.fft.fftshift(np.fft.irfft2(fa * fb, s=a.shape))


def main():
    print(f"grid {N} x {DX} px = {N * DX:.0f} px wide; kernel beta 4.0 as E1b injected")
    print(f"{'clean fwhm':>10} {'clean beta':>10} {'kernel':>7} {'obs fwhm':>9} {'obs/clean':>9} {'quadrature':>11} {'quad/true':>9}")
    for clean_fwhm, clean_beta in ((2.15, 3.0), (2.15, 5.0), (2.15, 8.0), (2.8, 5.0), (3.7, 5.0), (3.7, 8.0)):
        clean = grid_profile(clean_fwhm, clean_beta)
        assert abs(fwhm_of(clean) - clean_fwhm) < 0.02, fwhm_of(clean)
        for k in (0.5, 1.0, 2.0, 3.0, 4.0):
            kern = grid_profile(k, 4.0)
            obs = convolve(clean, kern)
            fo = fwhm_of(obs)
            quad = np.sqrt(max(fo * fo - clean_fwhm * clean_fwhm, 0.0))
            print(f"{clean_fwhm:10.2f} {clean_beta:10.1f} {k:7.1f} {fo:9.3f} {fo / clean_fwhm:9.3f} {quad:11.3f} {quad / k:9.3f}")
        print()


if __name__ == "__main__":
    main()
