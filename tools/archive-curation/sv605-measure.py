"""Group C filter measurement: SVBONY SV605CC + SH61 EDPH 270mm, 10 sessions.

Read-only. Same method as the validated group A/B pass (calibrated.py): bias MEASURED per
(gain, offset) per channel, three frames from the middle of each run, background B/G as the
discriminator and sky rate as corroboration. Constants here are this body's own, measured by
sv605-calibrate.py rather than borrowed from the ASI533:

    bias(gain=120, offset=20) = 804 ADU on all three channels (grey, so no white-balance undo)
    g = 0.2059 e-/ADU at gain=120   (flat-pair mean-variance, 0.6% spread over 3 sets)
    ADU scale: x4, a 14-bit sensor left-shifted into 16 bits, same as N.I.N.A. + ASI533

A second, independent measurement is included: the channel ratios of each FLAT set. A flat panel
is a fixed bright uniform source, so its ratios carry the passband with none of the sky's
confounds. It cannot name a filter on its own, but it can say whether two sessions were shot
through the SAME one.
"""
import csv, glob, os, warnings, collections
import numpy as np
warnings.filterwarnings("ignore")
from astropy.io import fits
from astropy.time import Time
from astropy.coordinates import EarthLocation, AltAz, SkyCoord, get_body, get_sun
import astropy.units as u

SCR = os.path.dirname(os.path.abspath(__file__))
BIAS = {(120, 20): dict(R=804.0, G=804.0, B=804.0, n=300, src="Helix 2025-08-20 BIAS (300f)")}
G_MEASURED = 0.2059

# Reference bands, measured on the ASI533 (same IMX533 sensor family, so the CFA sampling of a
# passband is the same). resweep.py / filter-inference.md section 2.
BANDS = {
    "L-Ultimate 3nm": (0.44, 0.72),
    "L-Quad Enhance": (0.90, 1.20),
}


def classify(bg):
    for name, (lo, hi) in BANDS.items():
        if lo <= bg <= hi:
            return name
    return "UNRESOLVED"


def fits_in(d):
    return sorted(glob.glob(os.path.join(glob.escape(d), "*.fit*")))


def planes2d(a, pat="RGGB"):
    out = {}
    for i, ch in enumerate(pat):
        dy, dx = divmod(i, 2)
        out.setdefault(ch, []).append(a[dy::2, dx::2])
    return out


def chan_medians(path):
    with fits.open(path, memmap=False, do_not_scale_image_data=True) as hd:
        h = hd[0].header
        a = hd[0].data.astype(np.float64) + float(h.get("BZERO", 0.0)) * float(h.get("BSCALE", 1.0))
    pat = str(h.get("BAYERPAT", "RGGB"))
    med = {}
    for c, pls in planes2d(a, pat).items():
        med[c] = float(np.median(np.concatenate([p.ravel() for p in pls])))
    return h, med


def geometry(h):
    try:
        loc = EarthLocation(lat=float(h.get("SITELAT")) * u.deg,
                            lon=float(h.get("SITELONG")) * u.deg,
                            height=(h.get("SITEELEV") or 100) * u.m)
        t = Time(h["DATE-OBS"], scale="utc")
        fr = AltAz(obstime=t, location=loc)
        sun = get_sun(t)
        moon = get_body("moon", t, loc)
        return (sun.transform_to(fr).alt.deg, moon.transform_to(fr).alt.deg,
                float((1 - np.cos(sun.separation(moon).rad)) / 2))
    except Exception:
        return float("nan"), float("nan"), float("nan")


def measure_session(d):
    fs = fits_in(d)
    if not fs:
        return None, "no frames"
    mid = len(fs) // 2
    picks = sorted({max(0, mid - 1), mid, min(len(fs) - 1, mid + 1)})
    bgs, rgs, skies, hs = [], [], [], []
    for p in picks:
        try:
            h, med = chan_medians(fs[p])
        except Exception as e:
            continue
        key = (h.get("GAIN"), h.get("OFFSET"))
        if key not in BIAS:
            return None, f"no measured bias for gain={key[0]} offset={key[1]}"
        b = BIAS[key]
        sig = {c: med[c] - b[c] for c in "RGB"}
        if sig["G"] <= 0:
            continue
        wb = {c: b[c] / b["G"] for c in "RGB"}
        bgs.append((sig["B"] / sig["G"]) / wb["B"])
        rgs.append((sig["R"] / sig["G"]) / wb["R"])
        am = float(h.get("AIRMASS") or 1.0)
        skies.append(sum(sig.values()) * G_MEASURED / float(h["EXPTIME"]) / max(am, 1e-6))
        hs.append(h)
    if not bgs:
        return None, "no usable frame"
    h = hs[0]
    sun, moon_alt, illum = geometry(h)
    return dict(bg=float(np.median(bgs)), rg=float(np.median(rgs)),
                sky=float(np.median(skies)), n=len(fs), exp=float(h["EXPTIME"]),
                gain=h.get("GAIN"), offset=h.get("OFFSET"), temp=h.get("CCD-TEMP"),
                object=str(h.get("OBJECT", "") or "").strip(),
                alt=h.get("CENTALT"), am=float(h.get("AIRMASS") or 1.0),
                sun=sun, moon_alt=moon_alt, illum=illum), None


# ---------------------------------------------------------------------------------------
rows = list(csv.DictReader(open(os.path.join(SCR, "nextup-detail.csv"), encoding="utf-8")))
C = [r for r in rows if "SV605" in r["cam"]]
print("=" * 122)
print("GROUP C  --  SVBONY SV605CC + SH61 EDPH 270mm     bias 804 grey, g=0.2059 e-/ADU measured")
print("=" * 122)
print(f"{'date':<11}{'object':<26}{'frm':>5}{'exp':>5}{'tC':>6}{'am':>5}{'sky':>7}{'B/G':>7}{'R/G':>7}"
      f"{'sun':>6}{'moon':>10}  {'path tag':<12} verdict")
print("-" * 122)
out = []
for r in sorted(C, key=lambda r: r["date"]):
    v, e = measure_session(os.path.join("D:/Astro-Pics", r["dir"]).replace("\\", "/"))
    if not v:
        print(f"{r['date']:<11}{r['object'][:25]:<26}  SKIPPED: {e}")
        continue
    verdict = classify(v["bg"])
    t = v["temp"]
    print(f"{r['date']:<11}{v['object'][:25]:<26}{v['n']:>5}{v['exp']:>5.0f}"
          f"{(f'{float(t):+.0f}' if t is not None else '?'):>6}{v['am']:>5.2f}{v['sky']:>7.2f}"
          f"{v['bg']:>7.3f}{v['rg']:>7.3f}{v['sun']:>6.1f}"
          f"{v['illum']*100:>5.0f}% a{v['moon_alt']:>+3.0f}  {(r['tag'] or '-')[:11]:<12} {verdict}")
    v.update(date=r["date"], dir=r["dir"], tag=r["tag"], verdict=verdict, gib=r["gib"])
    out.append(v)

print()
b3 = [r for r in out if r["verdict"] == "L-Ultimate 3nm"]
bq = [r for r in out if r["verdict"] == "L-Quad Enhance"]
bu = [r for r in out if r["verdict"] == "UNRESOLVED"]
for label, g in (("in the 3nm band", b3), ("in the L-Quad band", bq), ("UNRESOLVED", bu)):
    if g:
        a = np.array([r["bg"] for r in g])
        print(f"  {label:<20} {len(g):>2} sessions  {sum(r['n'] for r in g):>5} frames  "
              f"B/G {a.min():.3f}..{a.max():.3f}  median {np.median(a):.3f}")

# ---- independent check: the flat sets ---------------------------------------------------
print()
print("=" * 122)
print("INDEPENDENT CHECK: channel ratios of each FLAT set (fixed panel, so ratios = passband)")
print("=" * 122)
FLATS = [
    ("Helix        (session had NO tag)", "D:/Astro-Pics/2025/2025-08-20 - Helix Nebula/FLAT"),
    ("Ha-OIII Cal  (tagged Ha-OIII)", "D:/Astro-Pics/2025/2025-10/Ha-OIII Cal/FLAT"),
    ("RGB Cal      (tagged RGB)", "D:/Astro-Pics/2025/2025-10/RGB Cal/FLAT"),
    ("2025-11-03   (session had NO tag)", "D:/Astro-Pics/2025/2025-10/2025-11-03/FLAT"),
    ("Statue Lib   (session had NO tag)", "D:/Astro-Pics/2026/2026-02-14 Statue of Liberty Nebula/2026-02-19/FLAT"),
]
print(f"{'flat set':<38}{'n':>4}{'exp':>8}{'R/G':>8}{'B/G':>8}   nearest match")
print("-" * 122)
sig = {}
for label, d in FLATS:
    fs = fits_in(d)
    if not fs:
        print(f"{label:<38}  MISSING")
        continue
    use = fs[:: max(1, len(fs) // 8)][:8]
    rg, bg, exp = [], [], None
    for p in use:
        h, med = chan_medians(p)
        exp = float(h["EXPTIME"])
        b = BIAS[(h.get("GAIN"), h.get("OFFSET"))]
        s = {c: med[c] - b[c] for c in "RGB"}
        if s["G"] > 0:
            rg.append(s["R"] / s["G"])
            bg.append(s["B"] / s["G"])
    if not rg:
        continue
    sig[label] = (float(np.median(rg)), float(np.median(bg)))
    print(f"{label:<38}{len(use):>4}{exp:>8.2f}{sig[label][0]:>8.3f}{sig[label][1]:>8.3f}")

if sig:
    print("\n  pairwise B/G distance (a flat set matches another only if the FILTER matched):")
    keys = list(sig)
    for i, a in enumerate(keys):
        for b in keys[i + 1:]:
            d = abs(sig[a][1] - sig[b][1])
            mark = "  SAME FILTER" if d < 0.03 else ("  different" if d > 0.15 else "  unclear")
            print(f"    {a[:30]:<32} vs {b[:30]:<32} dB/G {d:>6.3f}{mark}")

with open(os.path.join(SCR, "groupC-measured.csv"), "w", newline="", encoding="utf-8") as f:
    if out:
        cols = ["date", "object", "n", "gib", "exp", "gain", "offset", "temp", "am", "alt",
                "sky", "bg", "rg", "sun", "moon_alt", "illum", "tag", "verdict", "dir"]
        w = csv.DictWriter(f, fieldnames=cols, extrasaction="ignore")
        w.writeheader()
        w.writerows(out)
print("\nwrote groupC-measured.csv")
