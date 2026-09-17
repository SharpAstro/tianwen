"""E1d follow-up: is Moffat composition FLATTER than quadrature at small kernel widths (so an error in
the fitted observed/clean ratio is amplified in the inverse), and what fitted-ratio error explains
est-w reading 0.92 and est-c 0.65 of truth on the same fits at 1.1-1.3x?"""
import numpy as np
from moffat_quadrature import grid_profile, fwhm_of, convolve

def composed(c, cb, k, kb=4.0):
    return fwhm_of(convolve(grid_profile(c, cb), grid_profile(k, kb)))

def invert(c, cb, o, kb=4.0):
    lo, hi = 0.05, 4.0 * c
    for _ in range(40):
        m = 0.5 * (lo + hi)
        if composed(c, cb, m, kb) < o: lo = m
        else: hi = m
    return 0.5 * (lo + hi)

c = 2.15
print(f"clean {c} px; kernel beta 4; columns: true k/c, composed obs/clean, quadrature obs/clean, slope d(obs/clean)/d(k/c) both")
for cb in (2.5, 3.0, 4.0, 5.0):
    print(f"-- clean beta {cb}")
    for kc in (0.3, 0.45, 0.6, 0.8, 1.0, 1.3):
        k = kc * c
        o = composed(c, cb, k)
        oq = np.sqrt(c * c + k * k)
        dk = 0.02 * c
        so = (composed(c, cb, k + dk) - composed(c, cb, k - dk)) / (2 * dk) 
        sq = k / oq
        print(f"  k/c {kc:4.2f}  comp {o / c:6.3f}  quad {oq / c:6.3f}  slope comp {so:5.2f}  slope quad {sq:5.2f}")

print()
print("E1d 1.1-1.3x: the SAME fits gave est-w 0.92 and est-c 0.65 of the injected width. For a true k/c and")
print("clean beta, find the fitted obs/clean ratio r_fit that makes quadrature read 0.92k and see what")
print("composition reads off the same r_fit.")
for cb in (2.5, 3.0, 4.0):
    for kc in (0.5, 0.66, 0.8):
        k = kc * c
        o_true = composed(c, cb, k)
        r_true = o_true / c
        k_quad_true = np.sqrt(o_true**2 - c**2)
        # r_fit such that sqrt(r^2 - 1) = 0.92 k/c
        r_fit = np.sqrt(1 + (0.92 * kc) ** 2)
        k_comp_fit = invert(c, cb, r_fit * c)
        print(f"  beta {cb} k/c {kc:4.2f}: true r {r_true:5.3f} (quad reads {k_quad_true / k:4.2f}k); r_fit for quad 0.92k = {r_fit:5.3f} "
              f"(ratio error {r_fit / r_true - 1:+.1%}); composition off r_fit reads {k_comp_fit / k:4.2f}k")
