"""Does a remover's smooth plate hold structure the data supports? (2026-11-23, the owner on StarXTerminator's plate of the
Tarantula's crowded field: "do you think SXT is making up nebulosity here? it feels awfully smooth")

    python -I judge_floor_check.py <master> <the judge's plate> <our plate> [--size 400] [--at x,y] [--channel 1]

In one crop (default: the 400 px box where our plate stands furthest over the judge's), one channel (green), in the
master's sky noise (the pixel noise of its quietest twentieth of 32 px blocks):
  - the pixel noise of the input and of both plates, in the crop and in the frame's quietest tenth of blocks;
  - each plate's level over the frame's sky beside the input's median and 10th percentile;
  - the share of the crop a plate changed, lowered and RAISED by over one sky noise (a plate that makes light up raises);
  - the structure of 8 to 60 px (a Gaussian of 8 px less one of 60): each plate's against the input's own lower envelope
    (a running 15th percentile of 15 px, smoothed over 4 px: no network in it), their correlations and strengths, with
    and without the bright knots (over 5 sky noise in the judge's band).
There is no independent truth under a field this dense: agreement with the envelope says "consistent with the data".
"""
import argparse

import numpy as np
from astropy.io import fits
from scipy.ndimage import gaussian_filter, percentile_filter, uniform_filter

p = argparse.ArgumentParser()
p.add_argument("master")
p.add_argument("judge")
p.add_argument("ours")
p.add_argument("--size", type=int, default=400)
p.add_argument("--at", default=None)
p.add_argument("--channel", type=int, default=1)
a = p.parse_args()
B = 32


def plane(path):
    x = np.nan_to_num(np.asarray(fits.getdata(path), np.float64))
    return x if x.ndim == 2 else x[min(a.channel, x.shape[0] - 1)]


def pixel_noise(x):
    return 1.4826 * float(np.median(np.abs(np.diff(x, axis=1)))) / np.sqrt(2)


m, j, o = plane(a.master), plane(a.judge), plane(a.ours)
H, W = m.shape
bh, bw = H // B, W // B
block_noise = np.array([[pixel_noise(m[i * B:(i + 1) * B, k * B:(k + 1) * B]) for k in range(bw)] for i in range(bh)])
sky_noise = float(np.percentile(block_noise, 5))
level = np.median(m[:bh * B, :bw * B].reshape(bh, B, bw, B).transpose(0, 2, 1, 3).reshape(bh, bw, -1), axis=2)
sky = float(np.percentile(level, 5))
if a.at:
    x, y = (int(v) for v in a.at.split(","))
else:
    over = uniform_filter(o - j, a.size)
    over[:a.size] = over[-a.size:] = -1e9
    over[:, :a.size] = over[:, -a.size:] = -1e9
    y, x = np.unravel_index(int(np.argmax(over)), over.shape)
h = a.size // 2
crop = (slice(y - h, y + h), slice(x - h, x + h))
M, J, O = m[crop], j[crop], o[crop]
print(f"crop of {a.size} px at x {x} y {y}; frame sky {sky:.5f}, sky noise {sky_noise:.6f}")
print(f"pixel noise in the crop, in sky noise: input {pixel_noise(M) / sky_noise:.2f} | ours {pixel_noise(O) / sky_noise:.2f} | "
      f"judge {pixel_noise(J) / sky_noise:.2f}")
quiet = np.argwhere(block_noise <= np.percentile(block_noise, 10))
for name, img in (("input", m), ("ours", o), ("judge", j)):
    q = np.median([pixel_noise(img[i * B:(i + 1) * B, k * B:(k + 1) * B]) for i, k in quiet])
    print(f"  in the frame's quietest tenth of blocks: {name} {q / sky_noise:.2f}")
print(f"level over the frame's sky, in sky noise: input median {(np.median(M) - sky) / sky_noise:.1f}, input 10th percentile "
      f"{(np.percentile(M, 10) - sky) / sky_noise:.1f} | ours {(np.median(O) - sky) / sky_noise:.1f} | judge {(np.median(J) - sky) / sky_noise:.1f}")
for name, img in (("ours", O), ("judge", J)):
    print(f"{name}: changed by over one sky noise {100 * (np.abs(img - M) > sky_noise).mean():.0f} % of the crop, lowered "
          f"{100 * ((M - img) > sky_noise).mean():.0f} %, RAISED {100 * ((img - M) > sky_noise).mean():.1f} %")


def band(x):
    return gaussian_filter(x, 8) - gaussian_filter(x, 60)


envelope = gaussian_filter(percentile_filter(M, 15, size=15), 4)
BJ, BO, BE = band(J), band(O), band(envelope)
inner = (slice(40, -40), slice(40, -40))


def corr(u, v, sel=None):
    u, v = (u[inner], v[inner]) if sel is None else (u[sel], v[sel])
    return float(np.corrcoef(u.ravel(), v.ravel())[0, 1])


print(f"structure of 8 to 60 px, correlation: judge with ours {corr(BJ, BO):.2f} | judge with the input's lower envelope "
      f"{corr(BJ, BE):.2f} | ours with the envelope {corr(BO, BE):.2f}")
print(f"  its strength (rms, sky noise): judge {BJ[inner].std() / sky_noise:.2f} | ours {BO[inner].std() / sky_noise:.2f} | "
      f"envelope {BE[inner].std() / sky_noise:.2f}")
calm = np.abs(BJ) < 5 * sky_noise
print(f"outside the bright knots ({100 * calm.mean():.0f} % of the crop): judge with ours {corr(BJ, BO, calm):.2f} | judge with the "
      f"envelope {corr(BJ, BE, calm):.2f}; strength judge {BJ[calm].std() / sky_noise:.2f} | ours {BO[calm].std() / sky_noise:.2f} | "
      f"envelope {BE[calm].std() / sky_noise:.2f}")
