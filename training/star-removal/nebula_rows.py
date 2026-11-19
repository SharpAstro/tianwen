"""What the star rows cannot see on bright extended light (#2245): light ADDED over the input, broad light taken, and
the input's bright compact peaks still standing on nebula.

    python -I nebula_rows.py <master fits> <starless fits>
    python -I nebula_rows.py --dir <a scored folder> --config <scorecard json>

The first form prints one master's rows. The second reads every master of a folder `score_whole.py` wrote (the
`.post.fits` plate where one exists, else the plate), writes `nebula.txt` beside its `row.txt` and prints it.
`score_whole.py` runs the first form on every master and carries a short form of each line in its row, so no score
is made without these.

Why they exist. The owner found three defects by eye on 2026-11-19 that the scorecard had passed: flat patches where
the dig floor fills inside bright nebula, stars put back inside a protected core, and faint stars left on nebula. The
star rows ask whether a star's site is FLAT, which a flat patch is, and they pool every star on bright background, so
a core's few stars vanish in the count. These rows take the INPUT as their reference, not another remover:

  added      pixels of the nebula where the starless image stands over the input by more than 3 sigma of a 3 px mean.
             Removing stars can only take light, so every such pixel is a fill that put light in. Also read over the
             whole frame.
  broad      the light taken that is not compact: the 9 px median of (input - starless), what exceeds a third of a
             sigma, averaged over the nebula in sigma. A star's core does not survive a 9 px median; a hole dug under
             it, or a halo taken with it, does. Read beside a judge's figure, since a real halo counts here too.
  standing   of the INPUT's compact peaks on the nebula (local maxima of the 1 px less 4 px band over 50 sigma), the
             share that keeps over half its height in the starless image, and the same for the brightest tenth of
             them. A nebula knot is such a peak too and no remover takes it, so the figure has a floor that a judge's
             output shows; a star put back inside a protected core raises it over that floor.

Not caught, and said so wherever these rows are quoted: FAINT stars left on bright nebula. Peaks under 50 sigma
cannot be told from knots this way, and the detector's list holds too few of them there to carry a figure.

The nebula is where the input's large-scale map (8 px block medians, median-filtered over 7 blocks, as
`ExtendedCoreMask` builds it) stands 20 pixel sigmas over the sky (the map's 5th percentile). Luminance is the mean
of the channels.
"""
import argparse, glob, json, os
import numpy as np
from astropy.io import fits
from scipy.ndimage import gaussian_filter, maximum_filter, median_filter, uniform_filter

BLOCK, FILTER, NEBULA_SIGMA, BORDER = 8, 7, 20.0, 32
STRONG_SIGMA, PEAK_NOISE = 50.0, 0.28   # PEAK_NOISE: the noise of (gauss 1 px - gauss 4 px) for white pixel noise of 1


def luminance(path):
    a = np.asarray(fits.getdata(path), np.float32)
    return a if a.ndim == 2 else a[:3].mean(axis=0)


def rows(master, starless):
    """The rows of one master as a dict; `nebula_px` 0 when the frame holds no bright extended light."""
    inp, out = luminance(master), luminance(starless)
    if inp.shape != out.shape:
        raise SystemExit(f"{starless} is {out.shape}, the master {inp.shape}")
    valid = np.isfinite(inp) & np.isfinite(out)
    inp, out = np.nan_to_num(inp), np.nan_to_num(out)
    h, w = inp.shape
    valid[:BORDER] = valid[-BORDER:] = False; valid[:, :BORDER] = valid[:, -BORDER:] = False
    sig = 1.4826 * float(np.median(np.abs(np.diff(inp[::4], axis=1)))) / np.sqrt(2)
    bh, bw = h // BLOCK, w // BLOCK
    blocks = np.median(inp[:bh * BLOCK, :bw * BLOCK].reshape(bh, BLOCK, bw, BLOCK).transpose(0, 2, 1, 3).reshape(bh, bw, -1), axis=2)
    small = median_filter(blocks, FILTER)
    big = np.zeros_like(inp); big[:bh * BLOCK, :bw * BLOCK] = np.repeat(np.repeat(small, BLOCK, 0), BLOCK, 1)
    sky = float(np.percentile(small, 5))
    neb = valid & (big - sky > NEBULA_SIGMA * sig)
    n = int(neb.sum())
    r = {"sigma": sig, "nebula_px": n, "nebula_share": 100.0 * n / valid.sum()}
    over = uniform_filter(out - inp, 3) > sig          # 3 sigma of a 3 px mean, whose noise is sigma / 3
    r["added_frame"] = 100.0 * float(over[valid].mean())
    if n < 2000:
        r["nebula_px"] = 0
        return r
    bright = neb & (big >= np.percentile(big[neb], 90))
    r["added"] = 100.0 * float(over[neb].mean())
    r["added_bright"] = 100.0 * float(over[bright].mean())
    broad = np.clip(median_filter(inp - out, 9) - sig / 3, 0, None)
    r["broad"] = float(broad[neb].sum() / (n * sig))
    r["broad_bright"] = float(broad[bright].sum() / (bright.sum() * sig))
    # The input's own compact peaks on the nebula, not a detector's list: remover_check2's sources thin out on bright
    # nebula (two in the Lagoon's brightest tenth), and the stars a protected core gets back are exactly the ones it lacks.
    band = lambda a: gaussian_filter(a, 1) - gaussian_filter(a, 4)
    di, do = band(inp), maximum_filter(band(out), 3)
    s_band = PEAK_NOISE * sig
    peak = (di == maximum_filter(di, 7)) & neb & (di > STRONG_SIGMA * s_band)
    amp, kept = di[peak], do[peak] / di[peak]
    r["peaks"] = int(peak.sum())
    if r["peaks"] >= 20:
        top = amp >= np.percentile(amp, 90)
        r["standing"] = 100.0 * float((kept > 0.5).mean())
        r["standing_top"] = 100.0 * float((kept[top] > 0.5).mean())
    else:
        r["standing"] = r["standing_top"] = float("nan")
    return r


def line(name, r):
    if not r["nebula_px"]:
        return f"neb {name}: no bright extended light; added over the frame {r['added_frame']:.2f} %"
    return (f"neb {name}: nebula {r['nebula_px']} px ({r['nebula_share']:.1f} % of the frame) | added {r['added']:.2f} % "
            f"(brightest tenth {r['added_bright']:.2f} %, frame {r['added_frame']:.2f} %) | broad taken {r['broad']:.2f} "
            f"(brightest tenth {r['broad_bright']:.2f}) | of {r['peaks']} compact peaks of the input over {STRONG_SIGMA:.0f} sigma, "
            f"standing {r['standing']:.1f} %, of the brightest tenth {r['standing_top']:.1f} %")


def short(name, r):
    """The figures the scorecard's row carries: added %, broad taken, and the brightest peaks left standing %."""
    if not r["nebula_px"]:
        return f"{name} neb none"
    return f"{name} neb added {r['added']:.1f}% broad {r['broad']:.2f} peaks standing {r['standing']:.1f}/{r['standing_top']:.1f}"


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("paths", nargs="*", help="<master> <starless>")
    p.add_argument("--dir"); p.add_argument("--config")
    p.add_argument("--name", default="m")
    a = p.parse_args()
    if a.dir:
        cfg = json.load(open(a.config))
        text = []
        for name, master in cfg["masters"].items():
            plate = next((q for q in (f"{a.dir}/{name}_starless.post.fits", f"{a.dir}/{name}_starless.fits") if os.path.exists(q)), None)
            if plate is None:
                text.append(f"neb {name}: no plate in {a.dir}")
                continue
            r = rows(master, plate)
            text.append(line(name, r)); text.append("  short: " + short(name, r))
        open(f"{a.dir}/nebula.txt", "w", encoding="utf-8", newline="\n").write("\n".join(text) + "\n")
        print("\n".join(text))
    else:
        if len(a.paths) != 2:
            p.error("give <master> <starless>, or --dir with --config")
        r = rows(*a.paths)
        print(line(a.name, r)); print("short: " + short(a.name, r))
