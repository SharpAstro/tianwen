"""E2.10: how many subs per session the green-plane profile fit actually measured, from the PSF store.

    python tools/psf-green-counts.py <bake>/stats/psf-sessions.jsonl

Reads the store the way DatasetPsfStore does (last record per session wins) and prints, per session,
the sub count, how many carry a finite mosaic `SubFwhm` and a finite `SubFwhmGreen`, and the green
p10 / p50 / p90. A session whose green column is empty was refused by the fit, not measured sharp or
soft; that count is what `store-green-counts-guard.txt` in docs/plans/deconvolver-training.md is."""
import json, sys, collections
path = sys.argv[1]
last = collections.OrderedDict()
keys = None
with open(path, encoding="utf-8") as f:
    for line in f:
        line = line.strip()
        if not line:
            continue
        rec = json.loads(line)
        if keys is None:
            keys = list(rec.keys())
        sid = rec.get("SessionId") or rec.get("Session") or rec.get("Id")
        last[sid] = rec
print("keys:", [k for k in keys if "Sub" in k or "Session" in k or "Fwhm" in k][:40])
def finite(v):
    return isinstance(v, (int, float)) and v == v
tot_g = tot_m = tot_n = 0
rows = []
for sid, rec in last.items():
    g = rec.get("SubFwhmGreen") or []
    m = rec.get("SubFwhm") or []
    n = max(len(g), len(m))
    fg = sum(1 for v in g if finite(v)); fm = sum(1 for v in m if finite(v))
    tot_g += fg; tot_m += fm; tot_n += n
    gs = sorted(v for v in g if finite(v))
    p = lambda q: gs[int(q*(len(gs)-1))] if gs else float('nan')
    rows.append((sid, n, fm, fg, p(0.1), p(0.5), p(0.9)))
print(f"sessions {len(rows)}, subs {tot_n}, mosaic fits {tot_m} ({tot_m/max(tot_n,1):.0%}), green fits {tot_g} ({tot_g/max(tot_n,1):.0%})")
print(f"{'session':60} {'subs':>5} {'mos':>5} {'grn':>5} {'g p10':>6} {'g p50':>6} {'g p90':>6}")
for sid, n, fm, fg, a, b, c in rows:
    flag = " <- SV605CC" if sid and "SV605CC" in sid else ""
    print(f"{str(sid)[:60]:60} {n:5d} {fm:5d} {fg:5d} {a:6.2f} {b:6.2f} {c:6.2f}{flag}")
