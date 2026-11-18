"""#1786: the noise of real master tiles against synthetic tiles, read at several pixel lags (2026-11-18).

    python -I noise_lags.py <real store> <synthetic export> [session substring ...]

Per session, per channel: sigma_k = 1.4826 x median |p(x+k) - p(x)| / sqrt 2 for k = 1, 2, 3, 4, 8 along rows, the
median over tiles. For white noise every k reads the same. For correlated noise sigma_k = sigma x sqrt(1 - r_k), so a
plateau at large k is the full sigma and sigma_1 over the plateau says how correlated neighbours are.
"""
import glob, os, sys
import numpy as np

REAL, SYN = sys.argv[1], sys.argv[2]
want = sys.argv[3:]
LAGS = (1, 2, 3, 4, 8)


def lagsig(a):
    out = []
    for k in LAGS:
        d = a[:, :, k:] - a[:, :, :-k]
        out.append([1.4826 * np.median(np.abs(d[c] - np.median(d[c]))) / np.sqrt(2) for c in range(a.shape[0])])
    return np.array(out)  # lags x channels


def read(path):
    a = np.fromfile(path, np.float16).astype(np.float32)
    n = a.size // (256 * 256)
    return a.reshape(n, 256, 256)


for sess in sorted(os.listdir(f"{SYN}/tiles")):
    if want and not any(w.lower() in sess.lower() for w in want):
        continue
    syn = sorted(f for f in glob.glob(f"{glob.escape(SYN)}/tiles/{glob.escape(sess)}/*_deg000.f16"))
    real = sorted(f for f in glob.glob(f"{glob.escape(REAL)}/tiles/{glob.escape(sess)}/*_master.f16") if not f.endswith(".sigma.f16"))
    if not syn or not real:
        continue
    s = np.median([lagsig(read(f)) for f in syn[:40]], axis=0).mean(axis=1)
    r = np.median([lagsig(read(f)) for f in real[:40]], axis=0).mean(axis=1)
    print(sess[:70])
    print("   lag          " + "".join(f"{k:>9d}" for k in LAGS))
    print("   real         " + "".join(f"{v:9.5f}" for v in r) + f"   lag1/lag8 {r[0] / r[-1]:.3f}")
    print("   synthetic    " + "".join(f"{v:9.5f}" for v in s) + f"   lag1/lag8 {s[0] / s[-1]:.3f}")
    print("   syn / real   " + "".join(f"{v:9.3f}" for v in s / r), flush=True)
