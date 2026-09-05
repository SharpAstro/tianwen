"""Amplitude on Gaia-CONFIRMED stars, reported apart from amplitude on unmatched compact detail.

`star_table` calls any 5x5 local maximum above median + 8 MAD a star, and measured against Gaia DR3
that is right on a sparse field (99.6 percent) and wrong on an emission nebula (30.0 percent). This
project's pool is 100 percent OSC narrowband, so the campaign's headline "faint-star amplitude" has
been a blend of two different quantities. Both matter -- a denoiser should scrub neither a star nor
real nebulosity -- but they are different claims and a model can trade them against each other.

What this does NOT change: every ranking measured on identical peaks. It changes what the numbers mean.

Two honesty rules the output enforces:
  - a cell whose session would not plate-solve is dropped from BOTH populations, never scored as if
    it were all structure;
  - the coincidence floor is printed PER SESSION as well as pooled, because it is catalogue density
    times the matching disc and the densities differ tenfold across one eval: on eval4b the pooled
    figure read 19.8 percent while eta Car's cells sat at 40, so a pooled number hides the field on
    which a confirmed-star fraction is unreadable.

Usage:
  python n2n_starsplit.py --cache <eval cache> --models slug=ckpt.pt [...] [--match 4,10]
"""
import argparse
import numpy as np
import torch

import gaia_starmask
import n2n_metrics as M
import n2n_smoke as S
from gaia_depth import ring_ratio
from gaia_starmask import MATCH_PX

# The per-session cap walks from the pool's BP 16 down to Gaia's practical limit while the analytic
# coincidence floor stays under this. Measured 2026-09-05 (gaia_depth.py): Horsehead reaches 21 at a
# 1.5 percent floor and gains 50 points of confirmed stars; the Rim Nebula at 5.7"/px is over 4 percent
# at 16 already and stays there.
AUTO_MAG_MAX = 21.0
AUTO_FLOOR_MAX = 0.05


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--cache', required=True)
    ap.add_argument('--models', nargs='+', required=True, help='slug=checkpoint.pt')
    ap.add_argument('--blend', default='0.2,0.4,0.7,1.0')
    ap.add_argument('--match', default='4,10', help='noise-removal percentages to compare AT')
    ap.add_argument('--mag-max', default='auto',
                    # Formatted twice: once here, and again by argparse when it renders help. A literal
                    # percent must therefore survive BOTH, so it is written %%%% and not %%.
                    help='Gaia BP cap: a number, or "auto" for the deepest cap per session (16 to %g) whose '
                         'coincidence floor stays under %.0f%%%%' % (AUTO_MAG_MAX, 100 * AUTO_FLOOR_MAX))
    ap.add_argument('--only', default=None,
                    help='comma-separated substrings; score only the sessions whose id contains one. '
                         'For an observer set that shares a night with an arm (eval4\'s Rim Nebula '
                         '2025-05-02 is a member of every arm X pair): state the exclusion, then apply it.')
    ap.add_argument('--per-session', action='store_true',
                    help='under each model, the noise removed at full strength per session')
    a = ap.parse_args()

    dev = torch.device('cuda' if torch.cuda.is_available() else 'cpu')
    mm, meta = S.open_cache(a.cache)
    halves = meta['has_halves']
    cells = [i for i in range(meta['train_cells'], meta['cells']) if halves[i]]
    if a.only:
        wanted = [t.strip().lower() for t in a.only.split(',') if t.strip()]
        before = len(cells)
        cells = [i for i in cells if any(t in meta['keys'][i][0].lower() for t in wanted)]
        print(f'--only {a.only!r}: {len(cells)} of {before} scored cells kept')

    auto = str(a.mag_max).lower() == 'auto'
    fetch_to = AUTO_MAG_MAX if auto else float(a.mag_max)
    print('building the Gaia star mask (solves each session master once, then caches)')
    mask = gaia_starmask.build(a.cache, mag_max=fetch_to, with_mag=True)
    covered = [k for k, i in enumerate(cells) if i in mask]
    if not covered:
        raise SystemExit('no scored cell has a solved session; nothing to split')
    print(f'{len(covered)} of {len(cells)} scored cells are covered by a solved plate\n')

    idx = [cells[k] for k in covered]
    masters = np.asarray(mm[idx, S.SLOT_MASTER], dtype=np.float32)
    half_a = np.asarray(mm[idx, S.SLOT_HALF_A], dtype=np.float32)
    half_b = np.asarray(mm[idx, S.SLOT_HALF_B], dtype=np.float32)
    lm, lb = masters.mean(axis=1)[..., 16:-16, 16:-16], half_b.mean(axis=1)[..., 16:-16, 16:-16]
    stars = M.star_table(lm)
    cell_area = lm.shape[1] * lm.shape[2]

    # The cap per session. A fixed BP 16 was the pool's AVERAGE detection depth, and on 2026-09-05 it
    # read three quarters of Horsehead's real stars and two thirds of the Statue of Liberty's as
    # "detail" (gaia_depth.py); a fixed BP 21 makes a 135 mm field unreadable (Rim Nebula floor 123
    # percent). So each session takes the deepest cap whose analytic floor stays under AUTO_FLOOR_MAX,
    # never shallower than the old 16, and the cap is printed beside the number it produced.
    session_of = {i: meta['keys'][i][0] for i in idx}
    cap_of = {}
    for sid in dict.fromkeys(session_of[i] for i in idx):
        if not auto:
            cap_of[sid] = fetch_to
            continue
        cells_here = [(t, i) for t, i in enumerate(idx) if session_of[i] == sid]
        cap = gaia_starmask.DEFAULT_MAG_MAX
        for trial in np.arange(gaia_starmask.DEFAULT_MAG_MAX, AUTO_MAG_MAX + 0.5, 1.0):
            floor = np.mean([(mask[i][:, 2] < trial).sum() * np.pi * MATCH_PX ** 2 / cell_area for _, i in cells_here])
            if floor <= AUTO_FLOOR_MAX or trial == gaia_starmask.DEFAULT_MAG_MAX:
                cap = float(trial)
            else:
                break
        cap_of[sid] = cap

    confirmed, compact, extended = [], [], []
    n_conf = n_comp = n_ext = 0
    chance = []
    per_session = {}
    ring_band = {}
    for t, i in enumerate(idx):
        ys, xs, snr = stars[t]
        sid = session_of[i]
        g = mask[i]
        g = g[g[:, 2] < cap_of[sid]]
        if len(g) and len(ys):
            d = np.hypot(ys[:, None] - g[None, :, 0], xs[:, None] - g[None, :, 1]).min(axis=1)
            hit = d <= MATCH_PX
        else:
            hit = np.zeros(len(ys), bool)
        # An unmatched peak is split by SHAPE against the session's own confirmed stars: the ring-to-peak
        # ratio at 2 px (gaia_depth.ring_ratio), and "extended" is broader than the confirmed 95th
        # percentile. On Horsehead that put 82 percent of the unmatched remainder in extended and the
        # noise check at chance; on eta Car 83 percent stayed compact (blends below the match radius).
        rr = ring_ratio(lm[t], ys, xs, float(np.median(lm[t]))) if len(ys) else np.zeros(0)
        band = ring_band.setdefault(sid, [])
        band.extend(rr[hit].tolist())
        confirmed.append((ys[hit], xs[hit], snr[hit]))
        compact.append((ys[~hit], xs[~hit], snr[~hit], rr[~hit]))   # split once every band is known
        n_conf += int(hit.sum())
        floor = len(g) * np.pi * MATCH_PX ** 2 / cell_area
        chance.append(floor)
        p = per_session.setdefault(sid, dict(cells=0, peaks=0, conf=0, floor=0.0, ext=0, comp=0))
        p['cells'] += 1; p['peaks'] += len(ys); p['conf'] += int(hit.sum()); p['floor'] += floor

    # Second pass: the extended cut needs each session's whole confirmed population.
    cut_of = {sid: (np.percentile(b, 95) if len(b) >= 50 else np.nan) for sid, b in ring_band.items()}
    pooled_cut = np.percentile(sum(ring_band.values(), []), 95)
    for t, i in enumerate(idx):
        ys, xs, snr, rr = compact[t]
        cut = cut_of[session_of[i]]
        cut = pooled_cut if np.isnan(cut) else cut
        ext = rr > cut
        compact[t] = (ys[~ext], xs[~ext], snr[~ext])
        extended.append((ys[ext], xs[ext], snr[ext]))
        n_comp += int((~ext).sum()); n_ext += int(ext.sum())
        p = per_session[session_of[i]]
        p['ext'] += int(ext.sum()); p['comp'] += int((~ext).sum())

    total = n_conf + n_comp + n_ext
    print(f"{'session':44s} {'cells':>5} {'peaks':>6} {'BP cap':>6} {'floor':>6} {'stars':>7} {'compact':>8} {'extended':>9} {'of ext.':>8}")
    for sid, p in per_session.items():
        print(f"{sid.split('|')[0][-44:]:44s} {p['cells']:5d} {p['peaks']:6d} {cap_of[sid]:6g} {100*p['floor']/p['cells']:5.1f}% "
              f"{100*p['conf']/max(p['peaks'],1):6.1f}% {100*p['comp']/max(p['peaks'],1):7.1f}% "
              f"{100*p['ext']/max(p['peaks'],1):8.1f}% {100*p['ext']/max(n_ext,1):7.1f}%")
    print(f'{total} peaks over the covered cells: {n_conf} Gaia-confirmed stars ({100*n_conf/total:.1f}%), '
          f'{n_comp} compact unmatched ({100*n_comp/total:.1f}%), {n_ext} extended ({100*n_ext/total:.1f}%)')
    print(f'pooled coincidence floor: {100*np.mean(chance):.1f}% -- a confirmed fraction near a session\'s '
          f'floor is luck, not stars; "of ext." says which fields the extended column is made of.\n'
          f'Compact unmatched peaks are star-shaped: uncatalogued or blended stars, or knots; only the '
          f'EXTENDED column is nebulosity, and only it supports a structure claim.\n')

    raw = S.crop(half_a)
    pops = {'Gaia stars': confirmed, 'compact unmatched': compact, 'extended': extended}
    base_noise = float(np.mean([M.bg_stats(t)[1] for t in raw.mean(axis=1)]))
    raw_amp = {k: M.measure(raw.mean(axis=1), t, lb)[0][0] for k, t in pops.items()}

    alphas = [float(x) for x in a.blend.split(',') if x.strip()]
    targets = [float(x) for x in a.match.split(',') if x.strip()]
    # The last column is the model at FULL strength (the last --blend, 1.0 by default): how much noise
    # it removes at all on these cells, and what that costs. A match point a model never reaches prints
    # a dash, and on eval4's Horsehead and Statue cells the supervised warped arm and every arm X seed
    # stayed under 4 percent, so without this column the whole comparison was dashes.
    print(f"{'model':14s} " + ' '.join(f'{t:>19.0f}% removed' for t in targets) + f"{'full strength':>36}")
    print(f"{'':14s} " + ' '.join(f'{"stars/compact/extended":>27}' for _ in targets)
          + f"{'removed: stars/compact/extended':>36}")
    for spec in a.models:
        slug, ckpt = spec.split('=', 1)
        out = S.crop(S.denoise(a.cache, ckpt, half_a, dev))
        pts = {k: [(0.0, 0.0)] for k in pops}
        for al in alphas:
            blend = raw + al * (out - raw)
            la = blend.mean(axis=1)
            removed = (1.0 - float(np.mean([M.bg_stats(t)[1] for t in la])) / base_noise) * 100.0
            for k, t in pops.items():
                amp = M.measure(la, t, lb)[0][0]
                pts[k].append((removed, (raw_amp[k] - amp) / raw_amp[k] * 100.0))
        row = []
        for tgt in targets:
            vals = []
            for k in pops:
                p = sorted(pts[k]); hit = '  -'
                for (r0, s0), (r1, s1) in zip(p, p[1:]):
                    if r0 <= tgt <= r1 and r1 > r0:
                        hit = f'{s0 + (s1 - s0) * (tgt - r0) / (r1 - r0):5.1f}'
                        break
                vals.append(hit)
            row.append(' /'.join(f'{v:>8}' for v in vals))
        full_removed = pts['Gaia stars'][-1][0]
        full = f"{full_removed:5.1f}%: " + '/'.join(f'{pts[k][-1][1]:5.1f}' for k in pops)
        print(f'{slug:14s} ' + ' '.join(f'{r:>27}' for r in row) + f'{full:>36}')
        if a.per_session:
            # The pooled "removed" is a mean over cells of several fields, and a model can denoise one
            # field while making another NOISIER (arm X: +4 percent pooled on eval4b, -13 to -25 on
            # eval4's Horsehead and Statue). Per session, at full strength, so a pooled figure cannot
            # average an off-pool failure away.
            lo = (raw + alphas[-1] * (out - raw)).mean(axis=1)
            per_cell = 1.0 - np.array([M.bg_stats(t)[1] for t in lo]) / np.array([M.bg_stats(t)[1] for t in raw.mean(axis=1)])
            by_session = {}
            for t, i in enumerate(idx):
                by_session.setdefault(session_of[i], []).append(per_cell[t])
            print(f'{"":14s} full strength per session: ' + '  '.join(
                f"{sid.split('|')[0].split('/')[-1][-24:]} {100 * np.mean(v):+5.1f}%" for sid, v in by_session.items()))
    print('\nEach cell is the faint amplitude SPENT to buy that much quiet: lower is better, and the '
          'three numbers are the same model judged on Gaia stars, on compact unmatched peaks (uncatalogued '
          'or blended stars, knots) and on extended peaks (nebulosity). A structure claim rests on the third.')


if __name__ == '__main__':
    main()
