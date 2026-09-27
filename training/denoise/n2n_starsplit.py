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

# The two rules the 2026-09-26 audit (--audit-extended) added; main() says why beside them.
FLOOR_MARGIN = 2.0     # a session's confirmed fraction must clear this times its coincidence floor
BLEND_PX = 2.5         # a broad unmatched peak this close to a catalogued star is a blend
BLEND_DEEPER = 1.0     # "catalogued" reaches this many magnitudes past the session's match cap

# Detail kept, added 2026-09-28 after E15's 1:1 sheets showed every model, the shipped one included,
# smoothing away the Orion Nebula's bright filaments while no column here moved: the columns above score
# LOCAL MAXIMA, and a filament is a ridge. Per band of the H2 decomposition and per brightness level, the
# share of the signal's band power the output keeps, against the independent half B:
#   kept = sum(Y_b * B_b) / sum(A_b * B_b)
# With A = S + nA and B = S + nB, nB is independent of A and of the model's output, so the numerator is
# E[Y_b S_b] and the denominator E[S_b^2]: 1.0 keeps the band's signal (the identity and a perfect
# denoiser both do), below 1 smooths it away, above 1 amplifies it. Removing noise cannot move it, which
# is the point. The level of a pixel is B's own low-pass (independent of A); star-like peaks are masked,
# since the columns above already score them; and a bin whose denominator is not clearly signal prints
# '-' (the DoG bands are spatially correlated, so the z threshold is set high rather than read as a test).
DETAIL_BANDS = ((0.0, 1.0), (1.0, 2.0), (2.0, 4.0))
DETAIL_LEVELS = (0.30, 0.45, 0.60)   # stretched units; the export stretch puts a frame's median at 0.25
DETAIL_LEVEL_SIGMA = 3.0
DETAIL_MASK_PX = 3
DETAIL_MIN_PIXELS = 2000
DETAIL_MIN_Z = 10.0


def audit_extended(idx, session_of, mask, cap_of, confirmed, compact, extended, crop_shape,
                   radius=2.5, deeper=1.0, randoms=200):
    """Is the EXTENDED column nebulosity, or stars the 1 px match missed? For each population, the
    fraction of peaks with a catalogued star (BP brighter than the session's cap + `deeper`) within
    `radius` px, and the fraction with two or more (a blend of catalogued stars), against the same two
    fractions at uniform random positions in the same cells, which is the chance rate that density
    alone produces. A population near its chance rate is not made of catalogued stars; one far above it
    is, whatever its shape says. Added 2026-09-26, when three of the four broadband fields read an
    extended cost equal to their star cost."""
    rng = np.random.default_rng(0)
    h, w = crop_shape
    rows = {}
    for t, i in enumerate(idx):
        sid = session_of[i]
        g = mask[i]
        g = g[g[:, 2] < cap_of[sid] + deeper]
        ry = rng.uniform(2, h - 2, randoms); rx = rng.uniform(2, w - 2, randoms)
        pops = {'stars': confirmed[t][:2], 'compact': compact[t][:2], 'extended': extended[t][:2],
                'random': (ry, rx)}
        r = rows.setdefault(sid, {k: [0, 0, 0] for k in pops})
        for k, (ys, xs) in pops.items():
            if len(ys) == 0:
                continue
            if len(g):
                d = np.hypot(ys[:, None] - g[None, :, 0], xs[:, None] - g[None, :, 1])
                near = (d <= radius).sum(axis=1)
            else:
                near = np.zeros(len(ys), int)
            r[k][0] += len(ys); r[k][1] += int((near >= 1).sum()); r[k][2] += int((near >= 2).sum())
    print(f'AUDIT: a catalogued star (BP < cap + {deeper:g}) within {radius:g} px; "2+" is two or more of them.')
    print(f"{'session':44s} {'':>4} " + ' '.join(f'{k:>19s}' for k in ('stars', 'compact', 'extended', 'random')))
    print(f"{'':44s} {'cap':>4} " + ' '.join(f'{"n  >=1  2+":>19s}' for _ in range(4)))
    for sid, r in rows.items():
        cells = []
        for k in ('stars', 'compact', 'extended', 'random'):
            n, one, two = r[k]
            cells.append(f'{n:6d} {100*one/max(n,1):5.1f}% {100*two/max(n,1):5.1f}%')
        print(f"{sid.split('|')[0][-44:]:44s} {cap_of[sid]:4g} " + ' '.join(f'{c:>19s}' for c in cells))
    print('Read EXTENDED against RANDOM on the same row: at the random rate the population is not catalogued '
          'stars; near the STARS column it is, and a structure claim resting on it is a claim about stars.')


def detail_bands(lum):
    """The DETAIL_BANDS of a stack of luminance tiles, one (n, h, w) array per band."""
    return [np.stack([M.dog(x, s1, s2) for x in lum]) for s1, s2 in DETAIL_BANDS]


def detail_bins(lb, stars, extended):
    """Per pixel, the index of its DETAIL_LEVELS bin by B's low-pass, or -1 where a star-like peak is
    masked. Every detected peak is masked except those the split called extended, so a session dropped
    from the split (no extended population) masks all of its peaks: conservative, never star-polluted."""
    from scipy.ndimage import gaussian_filter
    r = DETAIL_MASK_PX
    out = np.empty(lb.shape, np.int8)
    for t in range(len(lb)):
        b = np.digitize(gaussian_filter(lb[t], DETAIL_LEVEL_SIGMA), DETAIL_LEVELS).astype(np.int8)
        keep = set(zip(extended[t][0].tolist(), extended[t][1].tolist()))
        ys, xs, _ = stars[t]
        for y, x in zip(ys.tolist(), xs.tolist()):
            if (y, x) not in keep:
                b[max(0, y - r):y + r + 1, max(0, x - r):x + r + 1] = -1
        out[t] = b
    return out


def detail_reference(a_bands, b_bands, bins):
    """Per (band, level): the denominator sum(A_b * B_b), its z (sum over root sum of squares), and the
    pixel count, computed once for every model."""
    ref = []
    for k in range(len(DETAIL_BANDS)):
        prod = a_bands[k] * b_bands[k]
        row = []
        for lvl in range(len(DETAIL_LEVELS) + 1):
            sel = bins == lvl
            p = prod[sel]
            den = float(p.sum())
            z = den / (float(np.sqrt((p.astype(np.float64) ** 2).sum())) + 1e-30)
            row.append((den, z, int(sel.sum())))
        ref.append(row)
    return ref


def detail_kept(lum_out, b_bands, bins, ref):
    """Detail kept per (band, level) for one output, None where the bin is unreadable."""
    y_bands = detail_bands(lum_out)
    out = []
    for k in range(len(DETAIL_BANDS)):
        row = []
        for lvl in range(len(DETAIL_LEVELS) + 1):
            den, z, n = ref[k][lvl]
            if n < DETAIL_MIN_PIXELS or z < DETAIL_MIN_Z or den <= 0:
                row.append(None)
                continue
            row.append(float((y_bands[k] * b_bands[k])[bins == lvl].sum()) / den)
        out.append(row)
    return out


def detail_line(label, kept):
    """One line per output: the levels left to right, the bands of each level comma-separated. No
    slash and no '%:' anywhere, so the model-row readers (e13_read / e14_read / e15_read) never match it."""
    cells = []
    for lvl in range(len(DETAIL_LEVELS) + 1):
        cells.append(','.join('   -' if kept[k][lvl] is None else f'{kept[k][lvl]:4.2f}' for k in range(len(DETAIL_BANDS))))
    return f'{"":14s} detail kept {label:>10s}: ' + ' | '.join(cells)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--cache', required=True)
    ap.add_argument('--models', nargs='+', default=None, help='slug=checkpoint.pt (required unless --audit-extended)')
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
    ap.add_argument('--audit-extended', action='store_true',
                    help='score no model; instead report, per session, how often each population sits beside a '
                         'catalogued star, against random positions in the same cells (is "extended" nebulosity, '
                         'or blended stars?)')
    ap.add_argument('--legacy-split', action='store_true',
                    help='the split before 2026-09-26 (no floor rule, no blend rule), to reproduce an older table')
    a = ap.parse_args()
    if not a.models and not a.audit_extended:
        ap.error('--models is required unless --audit-extended')

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

    # Second pass: the extended cut needs each session's whole confirmed population. Two rules added
    # 2026-09-26 by the --audit-extended audit, both off under --legacy-split so an older table can be
    # reproduced:
    #  - a session whose confirmed fraction does not clear FLOOR_MARGIN times its coincidence floor has
    #    no usable catalogue match (the 24 mm Carina: 3.8 percent against a 5.3 floor, a TAN-only WCS
    #    over a 25 x 15 degree lens field), so it leaves the split as an unsolved one does, while its
    #    noise removal still counts;
    #  - a broad unmatched peak with a catalogued star (BP brighter than the cap + BLEND_DEEPER) within
    #    BLEND_PX is a blend the 1 px match missed, and goes to compact, never to extended (on the two
    #    SMC fields 84 and 87 percent of the extended peaks sat beside one, against 24 and 31 by chance).
    dropped = set()
    if not a.legacy_split:
        for sid, p in per_session.items():
            if p['conf'] / max(p['peaks'], 1) < FLOOR_MARGIN * p['floor'] / max(p['cells'], 1):
                dropped.add(sid)
    cut_of = {sid: (np.percentile(b, 95) if len(b) >= 50 else np.nan) for sid, b in ring_band.items()}
    live = [b for sid, b in ring_band.items() if sid not in dropped]
    pooled_cut = np.percentile(sum(live, []), 95) if sum(len(b) for b in live) else np.nan
    empty = (np.zeros(0, int), np.zeros(0, int), np.zeros(0))
    for t, i in enumerate(idx):
        sid = session_of[i]
        p = per_session[sid]
        ys, xs, snr, rr = compact[t]
        cut = cut_of[sid]
        cut = pooled_cut if np.isnan(cut) else cut
        ext = rr > cut
        if not a.legacy_split and ext.any():
            g = mask[i]
            g = g[g[:, 2] < cap_of[sid] + BLEND_DEEPER]
            if len(g):
                d = np.hypot(ys[:, None] - g[None, :, 0], xs[:, None] - g[None, :, 1]).min(axis=1)
                blend = ext & (d <= BLEND_PX)
                p['blend'] = p.get('blend', 0) + int(blend.sum())
                ext = ext & ~blend
        p['ext'] += int(ext.sum()); p['comp'] += int((~ext).sum())
        if sid in dropped:
            confirmed[t] = compact[t] = empty
            extended.append(empty)
            continue
        compact[t] = (ys[~ext], xs[~ext], snr[~ext])
        extended.append((ys[ext], xs[ext], snr[ext]))
        n_comp += int((~ext).sum()); n_ext += int(ext.sum())
    n_conf -= sum(per_session[sid]['conf'] for sid in dropped)

    total = n_conf + n_comp + n_ext
    print(f"{'session':44s} {'cells':>5} {'peaks':>6} {'BP cap':>6} {'floor':>6} {'stars':>7} {'compact':>8} {'extended':>9} {'of ext.':>8}")
    for sid, p in per_session.items():
        share = '   (out)' if sid in dropped else f"{100*p['ext']/max(n_ext,1):7.1f}%"
        print(f"{sid.split('|')[0][-44:]:44s} {p['cells']:5d} {p['peaks']:6d} {cap_of[sid]:6g} {100*p['floor']/p['cells']:5.1f}% "
              f"{100*p['conf']/max(p['peaks'],1):6.1f}% {100*p['comp']/max(p['peaks'],1):7.1f}% "
              f"{100*p['ext']/max(p['peaks'],1):8.1f}% {share}")
    for sid in dropped:
        p = per_session[sid]
        print(f"DROPPED from the split: {sid.split('|')[0][-44:]}: confirmed {100*p['conf']/max(p['peaks'],1):.1f}% does not "
              f"clear {FLOOR_MARGIN:g}x its {100*p['floor']/p['cells']:.1f}% floor, so no catalogue match is usable there; its noise "
              f"removal still counts")
    n_blend = sum(p.get('blend', 0) for sid, p in per_session.items() if sid not in dropped)
    if n_blend:
        print(f'{n_blend} broad unmatched peaks sat within {BLEND_PX:g} px of a catalogued star (BP < cap + {BLEND_DEEPER:g}) and '
              f'went to compact as blends, never extended')
    print(f'{total} peaks over the covered cells: {n_conf} Gaia-confirmed stars ({100*n_conf/max(total,1):.1f}%), '
          f'{n_comp} compact unmatched ({100*n_comp/max(total,1):.1f}%), {n_ext} extended ({100*n_ext/max(total,1):.1f}%)')
    print(f'pooled coincidence floor: {100*np.mean(chance):.1f}% -- a confirmed fraction near a session\'s '
          f'floor is luck, not stars; "of ext." says which fields the extended column is made of.\n'
          f'Compact unmatched peaks are star-shaped: uncatalogued or blended stars, or knots; only the '
          f'EXTENDED column is nebulosity, and only it supports a structure claim.\n')

    if a.audit_extended:
        audit_extended(idx, session_of, mask, cap_of, confirmed, compact, extended, lm.shape[1:])
        return

    raw = S.crop(half_a)
    pops = {'Gaia stars': confirmed, 'compact unmatched': compact, 'extended': extended}
    base_noise = float(np.mean([M.bg_stats(t)[1] for t in raw.mean(axis=1)]))
    raw_amp = {k: M.measure(raw.mean(axis=1), t, lb)[0][0] for k, t in pops.items()}

    from scipy.ndimage import gaussian_filter
    raw_lum = raw.mean(axis=1)
    d_bins = detail_bins(lb, stars, extended)
    d_a, d_b = detail_bands(raw_lum), detail_bands(lb)
    d_ref = detail_reference(d_a, d_b, d_bins)
    edges = ['<{:.2f}'.format(DETAIL_LEVELS[0])] + [f'{lo:.2f}-{hi:.2f}' for lo, hi in zip(DETAIL_LEVELS, DETAIL_LEVELS[1:])] + \
            ['>={:.2f}'.format(DETAIL_LEVELS[-1])]
    print('DETAIL KEPT, against half B: sum(Y*B)/sum(A*B) per band, 1.0 keeps the signal, below 1 smooths it away.')
    print(f'  levels (B low-pass): ' + ' | '.join(
        f'{e} {d_ref[0][lvl][2]:,} px, z {"/".join(f"{d_ref[k][lvl][1]:.0f}" for k in range(len(DETAIL_BANDS)))}'
        for lvl, e in enumerate(edges)))
    print(f'  each level shows bands {", ".join(f"{s1:g}-{s2:g} px" for s1, s2 in DETAIL_BANDS)}; star-like peaks masked '
          f'({int((d_bins < 0).sum()):,} px); "-" is a bin under {DETAIL_MIN_PIXELS} px or z {DETAIL_MIN_Z:g}')
    print(detail_line('gauss1 ref', detail_kept(np.stack([gaussian_filter(x, 1.0) for x in raw_lum]), d_b, d_bins, d_ref)))
    print()

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
        print(detail_line('full', detail_kept(out.mean(axis=1), d_b, d_bins, d_ref)))
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
