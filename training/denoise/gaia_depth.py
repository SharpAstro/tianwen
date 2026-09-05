"""Is a field's UNMATCHED detail nebulosity, or stars fainter than the BP 16 cut?

`n2n_starsplit.py` calls a peak a star when a Gaia DR3 star to BP 16 sits within 1 px, and everything
else "detail". On eval4b that split is readable because three of its four fields are star fields; on
eval4 the two nebula fields (Horsehead at 13.3 percent confirmed, the Statue of Liberty at 36) supply
93 percent of the detail column, and nothing in the split says whether their unmatched peaks are
nebulosity or stars the BP 16 catalogue does not hold. The BP 16 cut is what the pool's masters can
SEE on average (completeness 75 percent at 15-16, 15 at 16-17), but a cut chosen for the pool is not a
statement about one field, and on a sparse field a deeper catalogue costs almost nothing in floor.

Three measurements, each of which can contradict the others:

1. FORWARD, deeper. Re-match the same peaks at BP 16, 18 and 21. Every cap prints its coincidence
   floor, and the star count is corrected for chance: matched = S + (N - S) f, so S = (matched - N f)
   / (1 - f). The BP shell table says how many peaks each fainter shell claims BEYOND what chance would
   claim from the peaks still unmatched.
2. REVERSE, per BP bin. Of the Gaia stars inside the scored cells, how many have a peak within 1 px,
   minus the chance of a star landing on SOME peak. Summing count times recovery over bins gives the
   number of peaks that ARE catalogued stars, from the catalogue's side, with no cap to choose.
3. SHAPE against an external reference. For every peak, the ring-to-peak ratio at radius 2 px and the
   half-master asymmetry, and the CONFIRMED population's own 5th to 95th percentile is the star band.
   That is what the 2026-09-04 shape proxy lacked: it judged peaks against the population's own median,
   which is self-referential, whereas Gaia-confirmed stars are an external truth about what a star
   looks like on THIS plate. A peak outside the band is not a star; inside it is star-shaped, which a
   knot can also be, so the band bounds the star fraction from above and the reverse count from below.

Usage:
  python gaia_depth.py [--cache n2n-eval4] [--session Horsehead] [--mags 16,18,21]
"""
import argparse
import numpy as np

import gaia_starmask
import n2n_metrics as M
import n2n_paths
import n2n_smoke as S
from gaia_starmask import MATCH_PX

BORDER = S.BORDER
CROP = S.TILE - 2 * BORDER


def match(ys, xs, g):
    """Nearest catalogue star per peak: (hit mask, index into g). g is (n, >=2) as (y, x, ...)."""
    if len(g) == 0 or len(ys) == 0:
        return np.zeros(len(ys), bool), np.full(len(ys), -1)
    d = np.hypot(ys[:, None] - g[None, :, 0], xs[:, None] - g[None, :, 1])
    j = d.argmin(axis=1)
    return d[np.arange(len(ys)), j] <= MATCH_PX, j


def reverse_hit(g, ys, xs):
    """Per catalogue star: is there a peak within MATCH_PX."""
    if len(g) == 0 or len(ys) == 0:
        return np.zeros(len(g), bool)
    d = np.hypot(g[:, None, 0] - ys[None, :], g[:, None, 1] - xs[None, :]).min(axis=1)
    return d <= MATCH_PX


def ring_ratio(t, ys, xs, med):
    """(mean of the 16 pixels at Chebyshev radius 2 - median) / (peak - median).

    A star with FWHM near 2 px puts under a tenth of its peak at radius 2; nebulosity of any scale
    the eye would call structure puts most of it there. Peaks sit at least 4 px from the tile edge.
    """
    peak = t[ys, xs] - med
    ring = np.zeros(len(ys))
    n = 0
    for dy in range(-2, 3):
        for dx in range(-2, 3):
            if max(abs(dy), abs(dx)) == 2:
                ring += t[ys + dy, xs + dx] - med
                n += 1
    return ring / n / np.maximum(peak, 1e-12)


def half_asymmetry(a, b, ys, xs):
    """|A - B| / (A + B) of the two half-master amplitudes above their own medians.

    A feature both halves saw sits near the noise-to-amplitude ratio; a peak that one half made
    alone sits near 1. Real nebulosity and real stars are both in both halves.
    """
    aa = a[ys, xs] - np.median(a)
    bb = b[ys, xs] - np.median(b)
    return np.abs(aa - bb) / np.maximum(np.abs(aa + bb), 1e-12)


def local_background(t, ys, xs, med, mad, half=7):
    """Median of the (2 half + 1)^2 box around each peak minus the tile median, in darkest-half MADs."""
    out = np.empty(len(ys))
    h, w = t.shape
    for k, (y, x) in enumerate(zip(ys, xs)):
        box = t[max(0, y - half):min(h, y + half + 1), max(0, x - half):min(w, x + half + 1)]
        out[k] = (np.median(box) - med) / mad
    return out


def pct(x, q):
    return float(np.percentile(x, q)) if len(x) else float('nan')


def run_session(sid, idx, meta, mm, mags):
    masters = np.asarray(mm[idx, S.SLOT_MASTER], dtype=np.float32)
    half_a = S.crop(np.asarray(mm[idx, S.SLOT_HALF_A], dtype=np.float32)).mean(axis=1)
    half_b = S.crop(np.asarray(mm[idx, S.SLOT_HALF_B], dtype=np.float32)).mean(axis=1)
    lm = S.crop(masters.mean(axis=1))
    stars = M.star_table(lm)
    n_peaks = sum(len(s[0]) for s in stars)
    area = len(idx) * CROP * CROP
    print(f'\n=== {sid}\n{len(idx)} cells, {n_peaks} peaks ({1e6 * n_peaks / area:.0f} per Mpx)')

    deepest = max(mags)
    plate = gaia_starmask.stars_for_session(sid, mag_max=deepest, verbose=True, with_mag=True)
    if plate is None:
        print('  master will not solve; skipped')
        return
    # Per cell, the deepest catalogue in cropped tile coordinates, with BP.
    per_cell = []
    for i in idx:
        _, cx, cy = meta['keys'][i]
        y = plate[:, 0] - cy - BORDER
        x = plate[:, 1] - cx - BORDER
        keep = (y >= 0) & (y < CROP) & (x >= 0) & (x < CROP)
        per_cell.append(np.column_stack([y[keep], x[keep], plate[keep, 2]]))

    # 1. Forward at each cap, chance-corrected.
    print(f"\n{'BP cap':>7} {'Gaia in cells':>13} {'confirmed':>10} {'floor':>7} {'stars (chance-corrected)':>25}")
    hit_at = {}
    for cap in mags:
        n_gaia = n_hit = 0
        hits = []
        for (ys, xs, snr), g in zip(stars, per_cell):
            gc = g[g[:, 2] < cap]
            n_gaia += len(gc)
            h, _ = match(ys, xs, gc)
            hits.append(h)
            n_hit += int(h.sum())
        f = n_gaia * np.pi * MATCH_PX ** 2 / area
        s_est = (n_hit - n_peaks * f) / (1 - f)
        hit_at[cap] = hits
        print(f'{cap:>7g} {n_gaia:>13d} {100 * n_hit / n_peaks:>9.1f}% {100 * f:>6.2f}% '
              f'{s_est:>10.0f} = {100 * s_est / n_peaks:>5.1f}% of peaks')

    # Shells: what each fainter cap adds, against what chance predicts it should add.
    print(f"\n{'BP shell':>10} {'newly matched':>13} {'chance expects':>14} {'excess':>7}")
    caps = sorted(mags)
    for lo, hi in zip(caps, caps[1:]):
        new = 0
        chance = 0.0
        for k, (ys, xs, snr) in enumerate(stars):
            before = hit_at[lo][k]
            after = hit_at[hi][k]
            new += int((after & ~before).sum())
            g = per_cell[k]
            shell = ((g[:, 2] >= lo) & (g[:, 2] < hi)).sum()
            chance += (~before).sum() * shell * np.pi * MATCH_PX ** 2 / (CROP * CROP)
        print(f'{f"{lo:g}-{hi:g}":>10} {new:>13d} {chance:>14.0f} {new - chance:>7.0f}')

    # 2. Reverse: per BP bin, recovery of catalogue stars, chance-corrected, summed to expected star peaks.
    peak_density = n_peaks / area
    p_chance = peak_density * np.pi * MATCH_PX ** 2
    edges = [0, 12, 13, 14, 15, 16, 17, 18, 19, 20, deepest]
    print(f"\n{'Gaia BP':>8} {'in cells':>9} {'with a peak':>12} {'recovery':>9} {'star peaks':>11}"
          f"   (chance {100 * p_chance:.2f}% per star)")
    expected = 0.0
    all_g = np.concatenate(per_cell) if per_cell else np.empty((0, 3))
    all_rev = np.concatenate([reverse_hit(g, ys, xs) for g, (ys, xs, _) in zip(per_cell, stars)]) \
        if per_cell else np.zeros(0, bool)
    for lo, hi in zip(edges, edges[1:]):
        m = (all_g[:, 2] >= lo) & (all_g[:, 2] < hi)
        if m.sum() < 5:
            continue
        raw = all_rev[m].mean()
        rec = max(0.0, (raw - p_chance) / (1 - p_chance))
        expected += m.sum() * rec
        print(f'{f"{lo}-{hi}":>8} {m.sum():>9d} {100 * raw:>11.1f}% {100 * rec:>8.1f}% {m.sum() * rec:>11.0f}')
    print(f'expected catalogued-star peaks: {expected:.0f} of {n_peaks} = {100 * expected / n_peaks:.1f}%; '
          f'the remaining {100 * (1 - expected / n_peaks):.1f}% have no Gaia star to BP {deepest:g}')

    # 3. Shape, judged against the stars confirmed at the DEEPEST cap. The BP 16 population is nearly
    # all SNR > 100 on a deep field (618 of 689 on Horsehead), so it cannot say what a faint star looks
    # like; the deep population spans every SNR bucket, at a floor the table above quotes.
    ref_cap = deepest
    rr_c, rr_u, as_c, as_u, bg_c, bg_u, snr_u, snr_c = [], [], [], [], [], [], [], []
    for k, (ys, xs, snr) in enumerate(stars):
        if not len(ys):
            continue
        t = lm[k]
        med = float(np.median(t))
        mad = M.bg_stats(t)[1]
        rr = ring_ratio(t, ys, xs, med)
        asym = half_asymmetry(half_a[k], half_b[k], ys, xs)
        bg = local_background(t, ys, xs, med, mad)
        c = hit_at[ref_cap][k]
        u = ~hit_at[deepest][k]
        rr_c.append(rr[c]); rr_u.append(rr[u]); as_c.append(asym[c]); as_u.append(asym[u])
        bg_c.append(bg[c]); bg_u.append(bg[u]); snr_u.append(snr[u]); snr_c.append(snr[c])
    rr_c, rr_u = np.concatenate(rr_c), np.concatenate(rr_u)
    snr_c = np.concatenate(snr_c)
    as_c, as_u = np.concatenate(as_c), np.concatenate(as_u)
    bg_c, bg_u = np.concatenate(bg_c), np.concatenate(bg_u)
    snr_u = np.concatenate(snr_u)
    print(f'\nshape: BP<{ref_cap:g}-confirmed stars ({len(rr_c)}) against peaks unmatched to BP {deepest:g} ({len(rr_u)})')
    print(f"{'':28s} {'p5':>7} {'p25':>7} {'p50':>7} {'p75':>7} {'p95':>7}")
    for name, c, u in [('ring(r=2)/peak', rr_c, rr_u), ('half asymmetry', as_c, as_u),
                       ('local background (MAD)', bg_c, bg_u)]:
        print(f'{name + ", confirmed":28s} ' + ' '.join(f'{pct(c, q):7.2f}' for q in (5, 25, 50, 75, 95)))
        print(f'{name + ", unmatched":28s} ' + ' '.join(f'{pct(u, q):7.2f}' for q in (5, 25, 50, 75, 95)))
    lo, hi = pct(rr_c, 5), pct(rr_c, 95)
    in_band = (rr_u >= lo) & (rr_u <= hi)
    broader = rr_u > hi
    # Half asymmetry is noise over amplitude, so it falls with SNR on its own; the reference for an
    # unmatched peak is the confirmed stars in the SAME SNR bucket, or a faint knot is called noise for
    # being faint.
    noisy = np.zeros(len(as_u), bool)
    as_ref = {}
    for blo, bhi in M.BUCKETS:
        mc = (snr_c >= blo) & (snr_c < bhi)
        mu = (snr_u >= blo) & (snr_u < bhi)
        as_ref[(blo, bhi)] = pct(as_c[mc], 95) if mc.sum() >= 10 else float('nan')
        if mc.sum() >= 10:
            noisy[mu] = as_u[mu] > as_ref[(blo, bhi)]
    print(f'\nunmatched peaks inside the confirmed stars\' ring-ratio band [{lo:.2f}, {hi:.2f}]: '
          f'{100 * in_band.mean():.1f}% (star-shaped: faint stars or unresolved knots)')
    print(f'unmatched peaks BROADER than any confirmed star (ring ratio > {hi:.2f}): {100 * broader.mean():.1f}% '
          f'(extended: nebulosity, or a saturated plateau)')
    print(f'unmatched peaks with half asymmetry above the confirmed p95 of their OWN SNR bucket: '
          f'{100 * noisy.mean():.1f}% (5 percent of true features land there by construction)')
    print(f"{'SNR bucket':>12} {'confirmed':>10} {'unmatched':>10} {'in band':>8} {'broader':>8} {'noisy':>8} "
          f"{'asym p95 ref':>12} {'local bg p50':>13}")
    for blo, bhi in M.BUCKETS:
        m = (snr_u >= blo) & (snr_u < bhi)
        mc = (snr_c >= blo) & (snr_c < bhi)
        if m.sum():
            print(f'{f"{blo:g}-{bhi:g}":>12} {mc.sum():>10d} {m.sum():>10d} {100 * in_band[m].mean():>7.1f}% '
                  f'{100 * broader[m].mean():>7.1f}% {100 * noisy[m].mean():>7.1f}% '
                  f'{as_ref[(blo, bhi)]:>12.2f} {pct(bg_u[m], 50):>13.1f}')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--cache', default='n2n-eval4', help='cache name under the scratch root, or a path')
    ap.add_argument('--session', default=None, help='substring of the session id; default every session')
    ap.add_argument('--mags', default='16,18,21')
    a = ap.parse_args()
    cache = a.cache if '/' in a.cache or '\\' in a.cache else n2n_paths.cache(a.cache)
    mags = [float(x) for x in a.mags.split(',') if x.strip()]
    mm, meta = S.open_cache(cache)
    halves = meta['has_halves']
    cells = [i for i in range(meta['train_cells'], meta['cells']) if halves[i]]
    by_session = {}
    for i in cells:
        sid = meta['keys'][i][0]
        if a.session is None or a.session.lower() in sid.lower():
            by_session.setdefault(sid, []).append(i)
    if not by_session:
        raise SystemExit(f'no scored cell matches {a.session!r}')
    for sid, idx in by_session.items():
        run_session(sid, idx, meta, mm, mags)


if __name__ == '__main__':
    main()
