import numpy as np, n2n_smoke as S, sys, time
S_ = 'C:/temp/tianwen-scratch/'
def cond(t):
    lum = t.mean(axis=0).ravel().astype(np.float32)
    med = np.median(lum); q25 = np.percentile(lum, 25)
    return (med - q25) * 100.0, med
def report(cache, split, slot, label):
    t0 = time.time()
    mm, meta = S.open_cache(S_ + cache)
    n = meta['cells']; tc = meta['train_cells']
    rng = range(0, tc) if split == 'train' else range(tc, n)
    by = {}
    for i in rng:
        if slot != 1 and not meta['has_halves'][i]: continue
        c, m = cond(np.asarray(mm[i, slot], dtype=np.float32))
        by.setdefault(meta['keys'][i][0].split('|')[0].split('/')[-1][-28:], []).append((c, m))
    print(f'{label} [{cache} {split} slot {slot}] {time.time()-t0:.0f}s', flush=True)
    for sid, v in sorted(by.items()):
        v = np.array(v)
        print(f'   {sid:28s} n={len(v):3d}  cond plane p5/med/p95 {np.percentile(v[:,0],5):5.2f} {np.median(v[:,0]):5.2f} {np.percentile(v[:,0],95):5.2f}   level med {np.median(v[:,1]):.3f}', flush=True)
which = sys.argv[1]
if which == 'x':
    report('n2n-x', 'train', S.SLOT_HALF_A, 'X training input (night A half)')
    report('n2n-x', 'val', S.SLOT_HALF_A, 'X gate input (HD 74167 pair)')
elif which == 'ctl':
    report('n2n-x-ctl', 'train', 1, 'xctl training input (sub)')
    report('n2n-x-ctl', 'train', S.SLOT_HALF_A, 'xctl same nights, half A')
elif which == 'eval4b':
    report('n2n-e2-eval4b', 'val', S.SLOT_HALF_A, 'eval4b scored input (half A)')
elif which == 'eval4':
    report('n2n-eval4', 'val', S.SLOT_HALF_A, 'eval4 scored input (half A)')
if which == 'e2':
    report('n2n-e2-warped', 'train', 1, 'E2 S-warped training input (injected sub-depth tile)')
    report('n2n-e2-control', 'train', 1, 'E2 control training input (sub)')
    report('n2n-e2-control', 'train', S.SLOT_HALF_A, 'E2 control, half A')
    report('n2n-d8', 'train', 1, 'v19d (d8) training input (sub)')
    report('n2n-d8', 'train', S.SLOT_HALF_A, 'v19d (d8), half A')
