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

# A peak is an integer pixel and a Gaia position is not, so a real match sits within 0.71 px of
# quantisation plus the WCS residual: measured on eval4b, 96.7 percent inside 1.0 px and 99.6 inside
# 1.5 once the writer's one-pixel CRPIX offset was fixed (gaia_starmask docstring). The 2.5 px this
# used to be was covering that offset, at 6.25 times the coincidence floor.
MATCH_PX = 1.0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--cache', required=True)
    ap.add_argument('--models', nargs='+', required=True, help='slug=checkpoint.pt')
    ap.add_argument('--blend', default='0.2,0.4,0.7,1.0')
    ap.add_argument('--match', default='4,10', help='noise-removal percentages to compare AT')
    ap.add_argument('--mag-max', type=float, default=gaia_starmask.DEFAULT_MAG_MAX)
    a = ap.parse_args()

    dev = torch.device('cuda' if torch.cuda.is_available() else 'cpu')
    mm, meta = S.open_cache(a.cache)
    halves = meta['has_halves']
    cells = [i for i in range(meta['train_cells'], meta['cells']) if halves[i]]

    print('building the Gaia star mask (solves each session master once, then caches)')
    mask = gaia_starmask.build(a.cache, mag_max=a.mag_max)
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

    confirmed, detail = [], []
    n_conf = n_det = 0
    chance = []
    per_session = {}
    for t, i in enumerate(idx):
        ys, xs, snr = stars[t]
        g = mask[i]
        if len(g) and len(ys):
            d = np.hypot(ys[:, None] - g[None, :, 0], xs[:, None] - g[None, :, 1]).min(axis=1)
            hit = d <= MATCH_PX
        else:
            hit = np.zeros(len(ys), bool)
        confirmed.append((ys[hit], xs[hit], snr[hit]))
        detail.append((ys[~hit], xs[~hit], snr[~hit]))
        n_conf += int(hit.sum()); n_det += int((~hit).sum())
        # Coincidence floor for this cell: catalogue density x the tolerance disc. Analytic, and checked
        # against a shifted catalogue on eval4b (8.0 against 8.0 percent on the densest field at 1 px).
        floor = len(g) * np.pi * MATCH_PX ** 2 / (lm.shape[1] * lm.shape[2])
        chance.append(floor)
        p = per_session.setdefault(meta['keys'][i][0], dict(cells=0, peaks=0, conf=0, floor=0.0))
        p['cells'] += 1; p['peaks'] += len(ys); p['conf'] += int(hit.sum()); p['floor'] += floor

    total = n_conf + n_det
    print(f"{'session':44s} {'cells':>5} {'peaks':>6} {'confirmed':>9} {'floor':>6} {'of detail':>9}")
    for sid, p in per_session.items():
        print(f"{sid.split('|')[0][-44:]:44s} {p['cells']:5d} {p['peaks']:6d} {100*p['conf']/max(p['peaks'],1):8.1f}% "
              f"{100*p['floor']/p['cells']:5.1f}% {100*(p['peaks']-p['conf'])/max(n_det,1):8.1f}%")
    print(f'{total} peaks over the covered cells: {n_conf} Gaia-confirmed ({100*n_conf/total:.1f}%), '
          f'{n_det} unmatched detail ({100*n_det/total:.1f}%)')
    print(f'pooled coincidence floor: {100*np.mean(chance):.1f}% -- a confirmed fraction near a session\'s '
          f'floor is luck, not stars, and "of detail" says which fields the structure column is made of\n')

    raw = S.crop(half_a)
    pops = {'Gaia-confirmed stars': confirmed, 'unmatched detail': detail}
    base_noise = float(np.mean([M.bg_stats(t)[1] for t in raw.mean(axis=1)]))
    raw_amp = {k: M.measure(raw.mean(axis=1), t, lb)[0][0] for k, t in pops.items()}

    alphas = [float(x) for x in a.blend.split(',') if x.strip()]
    targets = [float(x) for x in a.match.split(',') if x.strip()]
    print(f"{'model':14s} " + ' '.join(f'{t:>10.0f}% removed' for t in targets))
    print(f"{'':14s} " + ' '.join(f'{"stars/detail":>18}' for _ in targets))
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
            row.append(f'{vals[0]:>8} /{vals[1]:>8}')
        print(f'{slug:14s} ' + ' '.join(f'{r:>18}' for r in row))
    print('\nEach cell is the faint amplitude SPENT to buy that much quiet: lower is better, and the '
          'two numbers are the same model judged on stars and on structure.')


if __name__ == '__main__':
    main()
