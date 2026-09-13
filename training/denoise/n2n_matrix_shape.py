"""Shape statistics over the matrix's 3x3 windows: input, E3.0, the prior at three blends, luminance-only.

    python n2n_matrix_shape.py --root C:/temp/e2/matrix --variant flat
"""
import argparse

import numpy as np
from astropy.io import fits

import n2n_deconv_gate as DG
import n2n_metrics as M
import n2n_operator as OP
import n2n_star_shape as SH


def rd(p):
    with fits.open(p, memmap=False) as h:
        return np.nan_to_num(np.asarray(next(x for x in h if x.data is not None).data, dtype=np.float32))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", default="C:/temp/e2/matrix")
    ap.add_argument("--variant", default="flat")
    ap.add_argument("--reference", default="C:/temp/e2/matrix/sharp_flat.fits")
    ap.add_argument("--offset", default="-144,-127", help="soft -> sharp pixel offset")
    ap.add_argument("--windows", default="all", help="all, or a comma list like g11,g00")
    a = ap.parse_args()
    inp = rd(f"{a.root}/{a.variant}.fits")
    arms = {"E3.0": rd(f"{a.root}/{a.variant}/e30.fits"), "prior": rd(f"{a.root}/{a.variant}/prior.fits")}
    ref = rd(a.reference)
    div = float(inp.max()); div = div if div > 1 else 1.0
    mins, betas = OP.session_stretch_params(inp, div)

    def st(d):
        u = d / div
        return np.stack([OP.mtf(betas[c], np.clip(u[c] - mins[c], 0, None).astype(np.float64)) for c in range(3)]).astype(np.float32)

    S_, W, H = 448, inp.shape[2], inp.shape[1]
    XS, YS = [200, (W - S_) // 2, W - 200 - S_], [200, (H - S_) // 2, H - 200 - S_]
    dx, dy = (int(v) for v in a.offset.split(","))
    wanted = None if a.windows == "all" else set(a.windows.split(","))
    I, R = st(inp), st(ref)
    A = {k: st(v) for k, v in arms.items()}
    print(f"{a.variant}: width/truth, stars, gate ring depth excess | skirt ratio, signed ring mean (MAD), Moffat misfit")
    for r in range(3):
        for c in range(3):
            name = f"g{r}{c}"
            if wanted and name not in wanted:
                continue
            x, y = XS[c], YS[r]
            sl = (slice(None), slice(y, y + S_), slice(x, x + S_))
            sr = (slice(None), slice(y + dy, y + dy + S_), slice(x + dx, x + dx + S_))
            T = R[sr].mean(axis=0)
            medT = float(np.median(T)); _, madT = M.bg_stats(T)
            ys, xs = DG.detect(T, medT, madT)
            tw = DG.star_fwhm(T, ys, xs, medT)
            Ii = I[sl]
            Li = Ii.mean(axis=0)
            null = DG.ring_excess(Li, ys, xs, tw, medT, madT)
            rows = {"input": Ii, "E3.0": A["E3.0"][sl], "prior": A["prior"][sl],
                    "prior blend 0.5": Ii + 0.5 * (A["prior"][sl] - Ii)}
            ratio = A["prior"][sl].mean(axis=0) / np.maximum(Li, 1e-4)
            rows["prior, luminance only"] = Ii * ratio[None]
            rows["reference"] = R[sr]
            print(f"  {name} ({x},{y}) truth {tw:.2f} px, {len(ys)} stars")
            for k, v in rows.items():
                lum = v.mean(axis=0)
                med = float(np.median(lum)); _, mad = M.bg_stats(lum)
                w = DG.star_fwhm(lum, ys, xs, med); oy, _ = DG.detect(lum, med, mad)
                e = DG.ring_excess(lum, ys, xs, tw, med, mad) - null
                sk, rm, mf = SH.shape_row(lum, T, ys, xs, tw, med, mad, medT)
                print(f"    {k:24s} {w / tw:6.3f} {len(oy) / len(ys):5.2f} {e:+6.2f} | skirt {sk:5.2f}  ring {rm:+6.2f}  misfit {mf:.4f}")


if __name__ == "__main__":
    main()
