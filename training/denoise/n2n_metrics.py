"""One metric suite for every N2N variant, reported per SNR bucket and per spatial scale.

Written because a single headline number kept being wrong in a different way each time:
PSNR rewarded erasing stars, and amplitude retention alone cannot explain "L2 shows MORE
faint stars" when L2 retains LESS amplitude. Both readings are true and they measure
different things, so both are reported:

  amp kept   peak amplitude retained at the master's own star positions. Photometric
             fidelity. 1.000 means nothing was lost. Blind to whether the star is visible.
  detect     fraction of those stars still standing 5 sigma above the DENOISED image's own
             noise floor. Visibility, which is what the eye judges. A model that halves a
             star while quartering the noise makes it MORE detectable, not less, so this
             number can rise while amp kept falls. That is not a contradiction.
  snr gain   amp kept / noise ratio. The scale-free summary of the two above.

Structure is measured with stars masked out, at four scales via difference-of-Gaussians,
because a model can hold star cores and a flat background while ironing out the nebulosity
that carries the picture. corr is the one that cannot be gamed: smoothing lowers it.
"""
import argparse
import json
import os

import numpy as np
import torch
from scipy.ndimage import binary_dilation, gaussian_filter, maximum_filter

import n2n_smoke as S

# No default cache ON PURPOSE. There are two prepared caches now, `n2n` (the calgated bake) and
# `n2n-ds` (darkscaled, 11 slots, what everything since v10 trains on), and the checkpoint paths are
# absolute, so passing a checkpoint from one while the TILES come from the other is silent and
# produces a plausible table. It happened: the v14 scoring ran cross-bake and reported an apparent
# 30% fabrication win for a config that has none, because a bake difference moves fabrication more
# than a config change does. Required argument beats a default that points at a sibling dataset.
#
# The tell, if it ever slips through again: the single-raw-sub floor is 21.2 spurious/tile on `n2n-ds`
# and 20.1 on `n2n`. Compare that line against the previous run before reading any comparison.
SCALES = [(1.0, 2.0), (2.0, 4.0), (4.0, 8.0), (8.0, 16.0)]
# Master SNR buckets. The faint end is where every failure so far has lived.
BUCKETS = [(8, 15), (15, 30), (30, 100), (100, 1e9)]
DETECT_SIGMA = 5.0


def bg_stats(t):
    """(median, MAD) of the darkest half, so nebulosity is not counted as noise."""
    lo = t[t <= np.percentile(t, 50)]
    med = np.median(lo)
    return float(np.median(t)), float(np.median(np.abs(lo - med)) + 1e-12)


def star_table(lm):
    """Per-tile star positions and master SNR, detected on the master."""
    out = []
    for t in lm:
        med = np.median(t)
        _, mad = bg_stats(t)
        det = (t >= maximum_filter(t, size=5)) & (t > med + 8 * mad)
        ys, xs = np.nonzero(det)
        ok = (ys > 3) & (ys < t.shape[0] - 4) & (xs > 3) & (xs < t.shape[1] - 4)
        ys, xs = ys[ok], xs[ok]
        out.append((ys, xs, (t[ys, xs] - med) / mad))
    return out


def measure(la, stars, lref):
    """amp-kept and detectability per SNR bucket, plus overall."""
    amp = {b: [] for b in range(len(BUCKETS))}
    det = {b: [] for b in range(len(BUCKETS))}
    for i, (ys, xs, snr) in enumerate(stars):
        if not len(ys):
            continue
        rmed = np.median(lref[i])
        _, rmad = bg_stats(lref[i])
        amed, amad = np.median(la[i]), bg_stats(la[i])[1]
        ref_amp = np.maximum(lref[i][ys, xs] - rmed, 1e-9)
        got_amp = la[i][ys, xs] - amed
        for b, (lo, hi) in enumerate(BUCKETS):
            sel = (snr >= lo) & (snr < hi)
            if not sel.any():
                continue
            amp[b].extend((got_amp[sel] / ref_amp[sel]).tolist())
            det[b].extend((got_amp[sel] > DETECT_SIGMA * amad).tolist())
    return ([float(np.median(amp[b])) if amp[b] else float("nan") for b in range(len(BUCKETS))],
            [float(np.mean(det[b])) if det[b] else float("nan") for b in range(len(BUCKETS))],
            [len(amp[b]) for b in range(len(BUCKETS))])


def dog(img, s1, s2):
    return gaussian_filter(img, s1) - gaussian_filter(img, s2)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--models", nargs="+", required=True, help="slug=checkpoint.pt")
    ap.add_argument("--cache", required=True,
                    help="prepared cache the TILES come from. Required, not defaulted; see the "
                         "note at the top of this file.")
    ap.add_argument("--json", help="default: <cache>/metrics.json")
    ap.add_argument("--skip-val-sessions", type=int, default=0,
                    help="drop the FIRST N val sessions from the report. Use it to match a "
                         "training run's --gate-sessions: a checkpoint chosen by a probe on a "
                         "session has spent that session's held-out-ness, so scoring it there "
                         "flatters it against models that were never selected at all")
    a = ap.parse_args()
    cache = a.cache
    out_json = a.json or os.path.join(cache, "metrics.json")

    meta = json.load(open(os.path.join(cache, "meta.json"), encoding="utf-8"))
    n, n_train = meta["cells"], meta["train_cells"]
    mm = S.open_tiles(cache, meta)
    val = list(range(n_train, n))
    if a.skip_val_sessions:
        spent = set(meta["val_sessions"][:a.skip_val_sessions])
        val = [i for i in val if meta["keys"][i][0] not in spent]
        meta = dict(meta, val_sessions=meta["val_sessions"][a.skip_val_sessions:])
        if not val:
            raise SystemExit("skipping that many val sessions leaves nothing to score")
    masters = np.asarray(mm[val, 0], dtype=np.float32)
    subs = np.asarray(mm[val, 1], dtype=np.float32)
    dev = torch.device("cuda")

    m_c, s_c = S.crop(masters), S.crop(subs)
    lm = m_c.mean(axis=1)
    stars = star_table(lm)
    total = sum(len(s[0]) for s in stars)
    print(f"held-out: {', '.join(meta['val_sessions'])}")
    print(f"{len(val)} cells, {total} master-detected stars\n")

    # Star-free mask from the master, generously dilated: what is left is nebulosity and sky.
    masks = []
    for t in lm:
        med, mad = np.median(t), bg_stats(t)[1]
        det = (t >= maximum_filter(t, size=5)) & (t > med + 5 * mad)
        masks.append(~binary_dilation(det, np.ones((9, 9), bool)))
    masks = np.array(masks)
    ref_band = {sc: np.array([dog(t, *sc) for t in lm]) for sc in SCALES}

    base_noise = float(np.mean([bg_stats(t)[1] for t in lm]))
    rows = []

    def add(label, arr, note):
        la = arr.mean(axis=1)
        noise = float(np.mean([bg_stats(t)[1] for t in la]))

        # What was REMOVED (input - output) should be noise, and noise correlates with
        # nothing. A positive correlation between the removed component and what survived
        # means structure went out with it. This needs no clean reference at all, which is
        # why it is the one diagnostic that would also work on a real unlabelled image.
        resid = []
        for i in range(len(la)):
            d = (lm[i] - la[i])[masks[i]]
            o = la[i][masks[i]]
            if d.std() > 0 and o.std() > 0:
                resid.append(float(np.corrcoef(d, o)[0, 1]))
        resid_corr = float(np.mean(resid)) if resid else float("nan")
        amp, det, cnt = measure(la, stars, lm)
        struct = []
        for sc in SCALES:
            band = np.array([dog(t, *sc) for t in la])
            r, c = [], []
            for i in range(len(band)):
                x, y = band[i][masks[i]], ref_band[sc][i][masks[i]]
                rx, ry = x.std(), y.std()
                r.append(rx / max(ry, 1e-12))
                if rx > 0 and ry > 0:
                    c.append(float(np.corrcoef(x, y)[0, 1]))
            struct.append((float(np.mean(r)), float(np.mean(c))))
        # Median of the per-bucket medians, so the bright end (which every model holds) cannot
        # drown out the faint end (where they differ) just by having more stars in it.
        overall_amp = float(np.median([v for v in amp if v == v]))
        rows.append({"label": label, "note": note, "noise": noise / base_noise,
                     "amp": amp, "detect": det, "counts": cnt, "struct": struct,
                     "resid_corr": resid_corr,
                     "snr_gain": overall_amp / (noise / base_noise)})
        return rows[-1]

    add("master (input)", m_c, "reference")
    add("single raw sub", s_c, "1 frame, no processing")
    for spec in a.models:
        slug, ckpt = spec.split("=", 1)
        add(slug, S.crop(S.denoise(cache, ckpt, masters, dev)), ckpt)

    hdr = "  ".join(f"{f'SNR {lo}-{hi if hi < 1e8 else 'inf'}':>13s}" for lo, hi in BUCKETS)
    print(f"{'model':22s} {'noise':>6s}  {hdr}")
    print(f"{'':22s} {'':>6s}  " + "  ".join(f"{'amp   detect':>13s}" for _ in BUCKETS))
    for r in rows:
        cells = "  ".join(f"{r['amp'][b]:5.2f} {r['detect'][b]:7.2f}" for b in range(len(BUCKETS)))
        print(f"{r['label']:22s} {r['noise']:5.2f}x  {cells}")

    print(f"\n{'model':22s} " + "  ".join(f"{f'{x:g}-{y:g}px':>13s}" for x, y in SCALES)
          + f"  {'resid corr':>11s}")
    print(f"{'':22s} " + "  ".join(f"{'ratio  corr':>13s}" for _ in SCALES)
          + f"  {'(0 = clean)':>11s}")
    for r in rows:
        cells = "  ".join(f"{a_:5.2f} {c_:6.3f}" for a_, c_ in r["struct"])
        print(f"{r['label']:22s} " + cells + f"  {r['resid_corr']:11.3f}")

    # ---- fabricated point sources -------------------------------------------------
    # The metric that overturned v6. Everything above asks whether real stars SURVIVE;
    # none of it asks whether new ones APPEAR. Measured on denoised SUBS, where the model
    # extrapolates hardest and where the invented grain is visible by eye. A dot is counted
    # as real if it lands on a master star (dilated 3x3 for centroid wobble).
    #
    # The threshold uses the WHOLE-TILE MAD, not bg_stats' darkest-half one. That is
    # deliberate and was got wrong once: the darkest-half MAD is the better NOISE estimator
    # (it excludes nebulosity) but as a detection bar it is far too low, and every model
    # then scores 18-25% real because the detections are noise in all of them -- the
    # discrimination vanishes. Swept over 5/8/12 sigma the ranking below is stable.
    DOT_SIGMA = 5.0

    def whole_mad(t):
        m = np.median(t)
        return m, float(np.median(np.abs(t - m))) + 1e-12

    truth = []
    for t in lm:
        med, mad = whole_mad(t)
        d = (t >= maximum_filter(t, size=5)) & (t > med + 8 * mad)
        truth.append(binary_dilation(d, np.ones((3, 3), bool)))
    truth = np.array(truth)

    def dots(arr):
        la = arr.mean(axis=1)
        tot = hit = 0
        for i in range(len(la)):
            t = la[i]
            med, mad = whole_mad(t)
            d = (t >= maximum_filter(t, size=5)) & (t > med + DOT_SIGMA * mad)
            ys, xs = np.nonzero(d)
            tot += len(ys)
            hit += int(truth[i][ys, xs].sum())
        n_t = len(la)
        return tot / n_t, (hit / tot if tot else float("nan")), (tot - hit) / n_t

    print(f"\nFabricated point sources, measured on denoised SUBS")
    print(f"{'model':22s} {'dots/tile':>10s} {'on a star':>10s} {'SPURIOUS/tile':>14s}")
    d_raw = dots(s_c)
    print(f"{'single raw sub':22s} {d_raw[0]:10.1f} {d_raw[1]:9.1%} {d_raw[2]:14.1f}")
    for spec in a.models:
        slug, ckpt = spec.split("=", 1)
        dn, frac, spur = dots(S.crop(S.denoise(cache, ckpt, subs, dev)))
        for r in rows:
            if r["label"] == slug:
                r["dots"], r["dots_real"], r["spurious"] = dn, frac, spur
        print(f"{slug:22s} {dn:10.1f} {frac:9.1%} {spur:14.1f}")

    print(f"\nstars per bucket: {rows[0]['counts']}")
    with open(out_json, "w", encoding="utf-8") as fh:
        json.dump({"buckets": BUCKETS, "scales": SCALES, "rows": rows}, fh, indent=1)
    print(f"wrote {out_json}")


if __name__ == "__main__":
    main()
