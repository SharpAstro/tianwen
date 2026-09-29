"""band1/band0 of NoiseField.Warped (replicated: R realisations, each white -> blur sigma -> random bilinear shift)."""
import numpy as np
from scipy.ndimage import gaussian_filter
rng = np.random.default_rng(3)
S = 288
def bands(f):
    b = [f] + [gaussian_filter(f, s, mode='nearest', truncate=3.0) for s in (1.0, 2.0, 4.0)]
    r = []
    for i in range(3):
        d = (b[i] - b[i + 1])[16:-16, 16:-16].ravel()
        r.append(1.4826 * np.median(np.abs(d - np.median(d))))
    return r
def warped(R, sigma):
    acc = np.zeros((S, S))
    for _ in range(R):
        one = rng.standard_normal((S, S))
        if sigma > 0:
            one = gaussian_filter(one, sigma, mode='nearest', truncate=3.0)
        dx, dy = rng.random() - 0.5, rng.random() - 0.5
        fx, fy = dx - np.floor(dx), dy - np.floor(dy); ix, iy = int(np.floor(dx)), int(np.floor(dy))
        p = np.pad(one, 2, mode='edge')
        def sh(ox, oy): return p[2 + iy + oy:2 + iy + oy + S, 2 + ix + ox:2 + ix + ox + S]
        acc += sh(0,0)*(1-fx)*(1-fy) + sh(1,0)*fx*(1-fy) + sh(0,1)*(1-fx)*fy + sh(1,1)*fx*fy
    return acc
print("white", round(np.median([bands(rng.standard_normal((S,S)))[1]/bands(rng.standard_normal((S,S)))[0] for _ in range(4)]),3))
for sigma in (0.0, 0.1, 0.2, 0.3, 0.4, 0.5):
    rs = []
    for _ in range(12):
        b = bands(warped(16, sigma)); rs.append(b[1] / b[0])
    print(f"sigma {sigma:.1f}: band1/band0 {np.median(rs):.3f}")
