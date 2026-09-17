"""What does a beta-4 Moffat kernel of nominal FWHM k, sampled on the 1 px grid, actually do to a
Moffat core? Compose each core with the DISCRETE kernel (deltas at integer offsets, weights from the
sampling rule) and invert the composed width back to the continuous kernel FWHM it is equivalent to.
Point sampling and 4x4 area sampling side by side; the probe's blur/truth column is the check."""
import numpy as np
from moffat_quadrature import grid_profile, fwhm_of, convolve, moffat, N, DX

def discrete_kernel(k, beta, radius, area_sub):
    """Weights on the integer grid; area_sub = 1 is point sampling at pixel centres."""
    ax = np.arange(-radius, radius + 1)
    w = np.zeros((ax.size, ax.size))
    offs = (np.arange(area_sub) + 0.5) / area_sub - 0.5
    for i, y in enumerate(ax):
        for j, x in enumerate(ax):
            acc = 0.0
            for oy in offs:
                for ox in offs:
                    acc += moffat(k, beta, np.hypot(x + ox, y + oy))
            w[i, j] = acc / (area_sub * area_sub)
    return ax, w / w.sum()

def place_discrete(ax, w):
    img = np.zeros((N, N))
    c = N // 2
    step = int(round(1.0 / DX))
    for i, y in enumerate(ax):
        for j, x in enumerate(ax):
            img[c + y * step, c + x * step] = w[i, j]
    return img

def composed_cont(c, cb, k, kb=4.0):
    return fwhm_of(convolve(grid_profile(c, cb), grid_profile(k, kb)))

def invert_cont(c, cb, o, kb=4.0):
    lo, hi = 0.02, 4.0 * c
    for _ in range(40):
        m = 0.5 * (lo + hi)
        if composed_cont(c, cb, m, kb) < o: lo = m
        else: hi = m
    return 0.5 * (lo + hi)

print(f"{'core':>5} {'beta':>4} {'nominal k':>9} | {'point: obs/c':>12} {'k_eff':>6} {'k_eff/k':>7} | {'area4: obs/c':>12} {'k_eff':>6} {'k_eff/k':>7} | {'continuous obs/c':>16}")
for c, cb in ((1.53, 3.0), (1.92, 3.0), (2.15, 3.0), (2.81, 3.0)):
    core = grid_profile(c, cb)
    for k in (0.5, 1.0, 2.0, 3.0, 4.0):
        cont = composed_cont(c, cb, k) / c
        cols = []
        for sub in (1, 4):
            ax, w = discrete_kernel(k, 4.0, radius=int(np.ceil(3 * k)) + 2, area_sub=sub)
            o = fwhm_of(convolve(core, place_discrete(ax, w)))
            keff = invert_cont(c, cb, o) if o > c * 1.001 else 0.0
            cols.append((o / c, keff, keff / k))
        (p_o, p_k, p_r), (a_o, a_k, a_r) = cols
        print(f"{c:5.2f} {cb:4.1f} {k:9.1f} | {p_o:12.3f} {p_k:6.2f} {p_r:7.2f} | {a_o:12.3f} {a_k:6.2f} {a_r:7.2f} | {cont:16.3f}")
    print()
