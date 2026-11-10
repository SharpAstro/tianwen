"""Real-star check 3 (2026-11-07): check 2 with its measurement errors fixed, and VALIDATED on the truth twin.

What check 2 got wrong, and what this does instead:
  1. Its reference ring sat at a FIXED 6-10 px. A bright or saturated star's halo reaches past 10 px, so the ring held
     starlight (left or dug) and the 'wing' was measured against it. Here the ring is sized per star: the radius where the
     INPUT's radial profile (every channel) falls under 0.5 sigma, clamped to 6..18 px, then a 3 px ring from there.
  2. It took the ring's MEDIAN as the sky under the star. In nebulosity the sky slopes across a 20 px window. Here a plane
     (a + bx + cy, one 3-sigma-clipped pass) is fitted to the ring per channel and read under the annulus.
  3. One noise plane for all three channels. Here each channel's own noise: 1.4826 MAD of the input's 5 px high-pass,
     per tile and channel.
  4. Neighbours inside the ring or annulus counted as wing. Here every catalogue star near the target is masked (radius by
     its band), and a target with a neighbour of a tenth its significance or more inside its reference ring is not scored.
  5. Real val holds Lobster and HIP 80609 three times each (two flips), M42 and SMC once. The pooled figure here weighs the
     four sessions equally.
  6. The wing is two regions: the 2-5 px ring the owner saw, and 5 px out to the reference ring (a saturated star's outer
     halo). Clean = every channel of both within 1 sigma of the plane.
Validation (--twin): f2-random-master injects synthetic stars into the SAME val masters' starless plates, so the true
output is known. For each scored star the verdict from the output alone is compared with the TRUTH verdict (the same regions
of output minus plate), and the plate itself is scored as a perfect remover (the measure's own false-flag rate).
Glue only; judge outputs (sxt) are judges, never targets.

    python -I real-star-check3.py [--twin] [--sessions] [model ...]
"""
import csv, json, sys
from collections import defaultdict
import numpy as np
from astropy.io import fits
from scipy.ndimage import median_filter

REALX = "C:/temp/tianwen-scratch/r2-real-val"; TWINX = "C:/temp/tianwen-scratch/f2-random-master"
EVAL = "C:/temp/tianwen-scratch/f2-eval-master"; SXT = "C:/temp/tianwen-scratch/sxt-real-val"
PLATES = "D:/Astro-Dataset/2026-10-12-full/starless/plates"
SIZE, CH, RIM, W = 256, 3, 16, 22
OWN = "--input-ring" not in sys.argv
# The plane is fitted on the OUTPUT's ring by default. The twin read (2026-11-07): on the INPUT's ring a bright star's faint
# halo lifts the plane, so a perfect output reads dug (10.6 % of saturated, 2.1 % >= 1000 sigma); on the output's ring the
# false flags stay at 1-2 % and the verdict agrees with the truth 92-97 %, but a dig wider than the ring is under-counted
# (1.8-2.9 % dug read against 5.7-6.1 % true at >= 1000 sigma).
BANDS = ["20-100", "100-1000", ">=1000", "saturated"]
yy, xx = np.mgrid[-W:W + 1, -W:W + 1]; rr = np.hypot(xx, yy)
A = (rr >= 2) & (rr <= 5); FOOT = rr <= 4; FAR = (rr >= 19) & (rr <= 22)

def band(z, sat):
    if sat: return "saturated"
    if not (z >= 20): return None
    return "20-100" if z < 100 else "100-1000" if z < 1000 else ">=1000"

def mask_r(z, sat): return 8 if sat or z >= 1000 else 5 if z >= 100 else 3
def read(p, shape): return np.fromfile(p, "<f2").reshape(shape).astype(np.float32)
def noise(img): return np.array([1.4826 * np.median(np.abs((h := c - median_filter(c, 5)) - np.median(h))) for c in img])

def r_in_of(win, ok, sig):
    """Radius where every channel of the input's star falls under 0.5 sigma (bin mean), against its own far ring."""
    far = np.array([np.median(win[c][FAR & ok]) for c in range(CH)])
    for r in range(5, 19):
        b = (rr >= r) & (rr < r + 1) & ok
        if b.sum() < 6: continue
        if (np.abs((win[:, b].mean(1) - far) / sig) < 0.5).all(): return max(r + 1, 6)
    return 18

def plane_fit(win, ring):
    """Per channel, a + b x + c y on the ring pixels, one 3-sigma-clipped pass; returns the plane over the window."""
    X = np.stack([np.ones(ring.sum()), xx[ring], yy[ring]], 1); out = np.empty_like(win)
    for c in range(CH):
        v = win[c][ring]; k = np.linalg.lstsq(X, v, rcond=None)[0]
        res = v - X @ k; s = 1.4826 * np.median(np.abs(res - np.median(res)))
        keep = np.abs(res) < 3 * max(s, 1e-12)
        if keep.sum() > 10: k = np.linalg.lstsq(X[keep], v[keep], rcond=None)[0]
        out[c] = k[0] + k[1] * xx + k[2] * yy
    return out

def measure(win, plane, a, b, ring, foot, sig):
    d = win - plane
    wa = d[:, a].mean(1) / sig
    wb = d[:, b].mean(1) / sig if b.sum() >= 8 else np.zeros(CH)
    tex = np.median(d[:, foot].std(1) / np.maximum(d[:, ring].std(1), 1e-12))
    return wa, wb, tex

def star_geometry(inp, x, y, k, S, sig):
    """Masks and regions for star k of S (an n x 5 array: x, y, z, saturated, mask radius) at pixel (x, y);
    None when a neighbour of a tenth its significance or more sits in its reference ring."""
    ok = np.ones_like(rr, bool)
    d = np.hypot(S[:, 0] - S[k, 0], S[:, 1] - S[k, 1])
    near = np.flatnonzero((np.abs(S[:, 0] - x) <= W + 8) & (np.abs(S[:, 1] - y) <= W + 8)); near = near[near != k]
    for j in near:
        ok &= np.hypot(xx - (S[j, 0] - x), yy - (S[j, 1] - y)) > S[j, 4]
    win = inp[:, y - W:y + W + 1, x - W:x + W + 1]
    rin = r_in_of(win, ok, sig)
    if ((S[near, 2] >= 0.1 * S[k, 2]) & (d[near] >= rin - 1) & (d[near] <= rin + 4)).any(): return None
    ring = (rr >= rin) & (rr <= rin + 3) & ok
    a, b = A & ok, (rr > 5) & (rr < rin) & ok
    if ring.sum() < 30 or a.sum() < 30: return None
    return rin, ring, a, b, FOOT & ok

def stats(v):
    wa, wb, tex = v[:, 0:3], v[:, 3:6], v[:, 6]
    worst = np.maximum(np.abs(wa).max(1), np.abs(wb).max(1))
    dug = ((wa < -1) | (wb < -1)).any(1); left = ((wa > 1) | (wb > 1)).any(1)
    return {"wing clean": 100 * np.mean(worst < 1), "ring dug": 100 * np.mean(dug), "ring left": 100 * np.mean(left),
            "worst p50": np.median(worst), "texture p50": np.median(tex), "n": len(v)}

# ---------------------------------------------------------------- the truth twin
_ls = {}
def lin_sigma(sid):
    """The session's linear sky noise, the median of its plate catalogue's sigma column (the 2026-09-29 bake the twin
    was exported from), so a twin star's band is read as a real star's is."""
    if sid not in _ls:
        p = f"D:/Astro-Dataset/2026-09-29-full/starless/plates/{sid.replace('/', '_').replace('|', '_')}_plate.stars.csv"
        _ls[sid] = float(np.nanmedian([float(r["sigma"]) for r in csv.DictReader(open(p, encoding="utf-8"))]))
    return _ls[sid]

def twin(models):
    rows = [json.loads(l) for l in open(f"{TWINX}/injections.jsonl", encoding="utf-8") if l.strip()]
    outs = {}
    for m in models:
        d = f"{EVAL}/{m}/on-random"
        outs[m] = {r["Tile"]: f"{d}/{r['Output']}" for r in map(json.loads, open(f"{d}/outputs.jsonl", encoding="utf-8"))}
    names = ["plate"] + models
    meas = {n: defaultdict(list) for n in names}; truth = {n: defaultdict(list) for n in names}
    old = {n: defaultdict(list) for n in names}
    for r in rows:
        if any(r["Tile"] not in outs[m] for m in models): continue
        p = f"{TWINX}/{r['Tile']}"; draw = read(p, (CH, SIZE, SIZE)); plate = read(p[:p.rindex("_deg")] + "_master.f16", (CH, SIZE, SIZE))
        sig = noise(draw)
        stars = []
        for s in r["Stars"]:
            x, y = s["X"], s["Y"]; xi, yi = int(round(x)), int(round(y))
            if not (0 <= xi < SIZE and 0 <= yi < SIZE): continue
            z = max(s["Amplitude"]) / lin_sigma(r["SessionId"])   # LINEAR significance, as the real catalogue's (tiles are stretched)
            stars.append((x, y, z, bool(s.get("Saturated")), mask_r(z, s.get("Saturated"))))
        S = np.array([st[:3] + (float(st[3]), st[4]) for st in stars]).reshape(-1, 5)
        imgs = {"plate": plate, **{m: read(outs[m][r["Tile"]], (CH, SIZE, SIZE)) for m in models}}
        sess = r["SessionId"].split("|")[2]
        for k, (x, y, z, sat, mr) in enumerate(stars):
            bnd = band(z, sat); xi, yi = int(round(x)), int(round(y))
            if bnd is None or not (RIM + W <= xi < SIZE - RIM - W and RIM + W <= yi < SIZE - RIM - W): continue
            if bnd == "20-100" and k % 5: continue   # a fifth of the faint band: it is the bulk and only a control here
            g = star_geometry(draw, xi, yi, k, S, sig)
            if g is None: continue
            rin, ring, a, b, foot = g
            sl = (slice(None), slice(yi - W, yi + W + 1), slice(xi - W, xi + W + 1))
            pl = plate[sl]
            ref = plane_fit(draw[sl], ring)
            for n, img in imgs.items():
                win = img[sl]
                wa, wb, tex = measure(win, plane_fit(win, ring) if OWN else ref, a, b, ring, foot, sig)
                meas[n][bnd].append(np.r_[wa, wb, tex])
                tw = (win - pl)
                truth[n][bnd].append(np.r_[tw[:, a].mean(1) / sig, tw[:, b].mean(1) / sig if b.sum() >= 8 else np.zeros(CH), 0])
                # check 2's measure: fixed 6-10 px ring median, 2-5 px annulus, no masks
                c2ring = (rr >= 6) & (rr <= 10)
                ow = (win[:, A].mean(1) - np.median(win[:, c2ring], 1)) / sig
                old[n][bnd].append(np.r_[ow, np.zeros(CH), 0])
    print("TWIN (f2-random-master, truth known). Rows: verdict from the output alone; 'agree' = same clean/not-clean as truth.")
    for bnd in BANDS:
        print(f"\n  {bnd}")
        print(f"    {'':12s} {'n':>6s} {'chk3 clean':>11s} {'TRUE clean':>11s} {'chk3 agree':>11s} {'chk2 clean':>11s} {'chk2 agree':>11s}  {'chk3 dug':>8s} {'TRUE dug':>8s}")
        for n in names:
            if not meas[n][bnd]: continue
            m3, t, o2 = np.array(meas[n][bnd]), np.array(truth[n][bnd]), np.array(old[n][bnd])
            c3 = np.maximum(np.abs(m3[:, :3]).max(1), np.abs(m3[:, 3:6]).max(1)) < 1
            ct = np.maximum(np.abs(t[:, :3]).max(1), np.abs(t[:, 3:6]).max(1)) < 1
            c2 = np.abs(o2[:, :3]).max(1) < 1
            d3 = ((m3[:, :6] < -1).any(1)); dt = ((t[:, :6] < -1).any(1))
            print(f"    {n[:12]:12s} {len(m3):6d} {100*c3.mean():11.1f} {100*ct.mean():11.1f} {100*(c3==ct).mean():11.1f}"
                  f" {100*c2.mean():11.1f} {100*(c2==ct).mean():11.1f}  {100*d3.mean():8.1f} {100*dt.mean():8.1f}")

# ---------------------------------------------------------------- real val
def real(models, by_session):
    rows = [json.loads(l) for l in open(f"{REALX}/injections.jsonl", encoding="utf-8") if l.strip()]
    def outputs(n):
        if n == "input": return {r["Tile"]: ("f16", f"{REALX}/{r['Tile']}") for r in rows}
        if n == "sxt": return {r["Tile"]: ("fits", f"{SXT}/in/{i:06d}_starless.fits") for i, r in enumerate(rows)}
        d = f"{EVAL}/{n}/on-real-val"
        return {r["Tile"]: ("f16", f"{d}/{r['Output']}") for r in map(json.loads, open(f"{d}/outputs.jsonl", encoding="utf-8"))}
    O = {n: outputs(n) for n in models}; cats = {}
    res = {n: defaultdict(list) for n in models}
    for r in rows:
        sid = r["SessionId"]
        if sid not in cats:
            cats[sid] = [(float(s["x"]), float(s["y"]), float(s["significance"]) if s["significance"] not in ("NaN", "") else 0.0,
                          s["saturated"] == "1", s["outcome"]) for s in
                         csv.DictReader(open(f"{PLATES}/{sid.replace('/', '_').replace('|', '_')}_plate.stars.csv", encoding="utf-8"))]
        inp = read(f"{REALX}/{r['Tile']}", (CH, SIZE, SIZE)); sig = noise(inp)
        stars = [(x - r["CellX"], y - r["CellY"], z, sat, mask_r(z, sat)) for x, y, z, sat, _ in cats[sid]
                 if -W - 8 <= x - r["CellX"] < SIZE + W + 8 and -W - 8 <= y - r["CellY"] < SIZE + W + 8]
        S = np.array([st[:3] + (float(st[3]), st[4]) for st in stars]).reshape(-1, 5)
        imgs = {}
        for n in models:
            kind, p = O[n][r["Tile"]]
            imgs[n] = read(p, (CH, SIZE, SIZE)) if kind == "f16" else np.asarray(fits.getdata(p), dtype=np.float32)
        sess = sid.split("|")[2]; flip = "flip=" in sid
        for k, (x, y, z, sat, mr) in enumerate(stars):
            bnd = band(z, sat); xi, yi = int(round(x)), int(round(y))
            if bnd is None or not (RIM + W <= xi < SIZE - RIM - W and RIM + W <= yi < SIZE - RIM - W): continue
            pass
            g = star_geometry(inp, xi, yi, k, S, sig)
            if g is None: continue
            rin, ring, a, b, foot = g
            sl = (slice(None), slice(yi - W, yi + W + 1), slice(xi - W, xi + W + 1))
            ref = plane_fit(inp[sl], ring)
            for n in models:
                win = imgs[n][sl]
                if np.isnan(win).any(): continue
                wa, wb, tex = measure(win, plane_fit(win, ring) if OWN else ref, a, b, ring, foot, sig)
                v = [*wa, *wb, tex]
                res[n][(bnd, sess)].append(v)
                if not flip: res[n][(bnd, sess, "unflipped")].append(v)
    sessions = sorted({k[1] for k in res[models[0]] if len(k) == 2})
    print("REAL VAL (no truth). Pooled = the four sessions weighted equally; n = stars scored over all sessions.")
    for bnd in BANDS:
        print(f"\n  {bnd}")
        print(f"    {'':16s}" + "".join(f"{n.replace('_synth18', '')[:12]:>13s}" for n in models))
        for key in ("wing clean", "ring dug", "ring left", "worst p50", "texture p50"):
            vals = []
            for n in models:
                per = [stats(np.array(res[n][(bnd, s)]))[key] for s in sessions if res[n].get((bnd, s))]
                vals.append(np.mean(per) if per else float("nan"))
            print(f"    {key:16s}" + "".join(f"{v:13.1f}" for v in vals))
        print(f"    {'n':16s}" + "".join(f"{sum(len(res[n].get((bnd, s), [])) for s in sessions):13d}" for n in models))
        if by_session:
            for s in sessions:
                if not res[models[0]].get((bnd, s)): continue
                print(f"      {s[:22]:22s} n={len(res[models[0]][(bnd, s)]):4d} clean" +
                      "".join(f"{stats(np.array(res[n][(bnd, s)]))['wing clean']:8.1f}" for n in models) + "   dug" +
                      "".join(f"{stats(np.array(res[n][(bnd, s)]))['ring dug']:7.1f}" for n in models))

if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    if "--twin" in sys.argv:
        twin(args or ["r3l_synth18_s0", "r3l_synth18_s1", "r3o_synth18_s0"])
    else:
        real(args or ["input", "sxt", "r3l_synth18_s0", "r3l_synth18_s1", "r3o_synth18_s0"], "--sessions" in sys.argv)
