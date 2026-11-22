"""A rebuilt starless-plate store against another one, from each store's own report lines (2026-11-23, #2449).

    python -I plate_store_compare.py <stored root>/stats/starless-plates.jsonl <rebuilt root>/stats/starless-plates.jsonl

Reads only the masters the second store holds. Every figure is the builder's own statistic
(`tianwen dataset starless-plates`), so the two sides differ by what the builder changed between the two runs and by
nothing in how they are read: a speckle is a subtracted star with a core pixel more than 4 sigma under the sky 6 to
12 px out, a hole is a core whose mean sits low, and each is read against the null the builder measures on the same
plate. The speckles are also split by the texture of the master under the star, each class against its own null, as
the plan's table of 2026-10-03 has them.
"""
import json
import sys
import numpy as np


def load(path):
    with open(path, encoding="utf-8") as f:
        return {r["Master"]: r for r in map(json.loads, f) if r.get("Statistics")}


old_all, new = load(sys.argv[1]), load(sys.argv[2])
names = [n for n in new if n in old_all]
old = {n: old_all[n] for n in names}
print(f"masters: {len(new)} in the second store, {len(names)} of them in the first")


def tot(store, f):
    return sum(f(store[n]) for n in names)


def line(label, f):
    a, b = tot(old, f), tot(new, f)
    print(f"  {label:<26} {a:>12,} -> {b:>12,}   ({(b - a) / a * 100:+.2f} %)" if a else f"  {label:<26} {a:>12,} -> {b:>12,}")


print("totals over those masters (first -> second):")
line("found", lambda r: r["Found"])
line("subtracted", lambda r: r["Subtracted"])
line("knots", lambda r: r["Knots"])
line("saturated", lambda r: r["Saturated"])
line("second pass", lambda r: r["SecondPass"])
for key in ("Found", "Subtracted", "Knots", "Saturated"):
    print(f"  masters with the same {key.lower()} count: {sum(1 for n in names if old[n][key] == new[n][key])} of {len(names)}")
dug = tot(new, lambda r: r["Statistics"].get("DugCores", 0))
print(f"  dug cores refilled in the second store: {dug:,} ({dug / tot(new, lambda r: r['Subtracted']) * 100:.1f} % of its subtracted stars)")

print("speckles by band of significance (speckled / sites, summed over the masters):")
bands = new[names[0]]["Statistics"]["Speckles"]["Bands"]
for i, b0 in enumerate(bands):
    out = []
    for store in (old, new):
        s = tot(store, lambda r: r["Statistics"]["Speckles"]["Bands"][i]["Speckled"])
        n = tot(store, lambda r: r["Statistics"]["Speckles"]["Bands"][i]["Sites"])
        out.append((s, n))
    (s0, n0), (s1, n1) = out
    print(f"  {b0['MinSignificance']:g} to {b0['MaxSignificance']:<9} {s0:>9,} / {n0:>10,} = {s0 / max(n0, 1) * 100:6.2f} %   ->   "
          f"{s1:>9,} / {n1:>10,} = {s1 / max(n1, 1) * 100:6.2f} %")
for lab, store in (("first", old), ("second", new)):
    s = tot(store, lambda r: r["Statistics"]["Speckles"]["Null"]["Speckled"])
    n = tot(store, lambda r: r["Statistics"]["Speckles"]["Null"]["Sites"])
    print(f"  null, {lab:<7} {s:>9,} / {n:>10,} = {s / max(n, 1) * 100:6.2f} %")

print("speckles by the texture under the star (median over masters with 50 sites or more, against the class's own null;")
print("in brackets the masters over twice their null):")
for ci, cls in enumerate(("smooth (under 1.2)", "textured (to 2)", "strongly textured")):
    for bi, b0 in enumerate(bands):
        out = []
        for store in (old, new):
            rates, nulls, over = [], [], 0
            for n in names:
                bg = store[n]["Statistics"]["Speckles"]["Backgrounds"][ci]
                b, nl = bg["Bands"][bi], bg["Null"]
                if b["Sites"] >= 50 and nl["Sites"] >= 50:
                    r, q = b["Speckled"] / b["Sites"], nl["Speckled"] / nl["Sites"]
                    rates.append(r)
                    nulls.append(q)
                    over += r > 2 * q and r > 0
            out.append((rates, nulls, over))
        (r0, q0, o0), (r1, q1, o1) = out
        if len(r1) < 3:
            continue
        print(f"  {cls:<20} {b0['MinSignificance']:g} to {b0['MaxSignificance']:<9} {np.median(r0) * 100:5.1f} against {np.median(q0) * 100:4.1f} "
              f"({o0} of {len(r0)})  ->  {np.median(r1) * 100:5.1f} against {np.median(q1) * 100:4.1f} ({o1} of {len(r1)})")

print("holes and inpainted cores by band (summed):")
sb = new[names[0]]["Statistics"]["Bands"]
for i, b0 in enumerate(sb):
    out = []
    for store in (old, new):
        h = tot(store, lambda r: r["Statistics"]["Bands"][i]["Holes"])
        n = tot(store, lambda r: r["Statistics"]["Bands"][i]["Subtracted"])
        k = tot(store, lambda r: r["Statistics"]["Bands"][i]["Inpainted"])
        out.append((h, n, k))
    (h0, n0, k0), (h1, n1, k1) = out
    print(f"  {b0['SigmaLow']:g} to {b0['SigmaHigh']:<9} holes {h0:>7,} -> {h1:>7,} ({h0 / max(n0, 1) * 100:.2f} % -> {h1 / max(n1, 1) * 100:.2f} %)"
          f"   stars with an inpainted core {k0:>9,} -> {k1:>9,} ({k0 / max(n0, 1) * 100:.1f} % -> {k1 / max(n1, 1) * 100:.1f} %)")
hn0 = np.array([old[n]["Statistics"]["HoleNullRate"] for n in names])
hn1 = np.array([new[n]["Statistics"]["HoleNullRate"] for n in names])
print(f"  hole null rate, median over masters: {np.median(hn0) * 100:.2f} % -> {np.median(hn1) * 100:.2f} %")

print("leftover point sources by band (the finder at 4 sigma on the finished plate; of which away from every subtracted star):")
for i, b0 in enumerate(sb):
    l0 = tot(old, lambda r: r["Statistics"]["Bands"][i]["Leftover"])
    l1 = tot(new, lambda r: r["Statistics"]["Bands"][i]["Leftover"])
    a0 = tot(old, lambda r: r["Statistics"]["Bands"][i]["LeftoverAway"])
    a1 = tot(new, lambda r: r["Statistics"]["Bands"][i]["LeftoverAway"])
    print(f"  {b0['SigmaLow']:g} to {b0['SigmaHigh']:<9} {l0:>8,} ({a0:>8,}) -> {l1:>8,} ({a1:>8,})   ({(l1 - l0) / max(l0, 1) * 100:+.1f} %)")

print("per master (median / 10th / 90th percentile; the largest change and its master):")


def per(label, f, scale=1.0, fmt="{:.2f}"):
    a = np.array([f(old[n]) for n in names], dtype=float) * scale
    b = np.array([f(new[n]) for n in names], dtype=float) * scale
    d = b - a
    q = lambda v: " / ".join(fmt.format(x) for x in np.nanpercentile(v, [50, 10, 90]))
    w = int(np.nanargmax(np.abs(np.where(np.isfinite(d), d, 0))))
    print(f"  {label:<24} first {q(a)}   second {q(b)}   change {q(d)}   largest {fmt.format(d[w])} ({names[w][:60]})")
    return a, b, d


ia, ib, idd = per("inpaint fraction, %", lambda r: r["Statistics"]["InpaintFraction"], 100)
for i, b0 in enumerate(sb):
    per(f"residual {b0['SigmaLow']:g}-{b0['SigmaHigh']}", lambda r: r["Statistics"]["Bands"][i]["ResidualMedian"])
for i, b0 in enumerate(sb):
    per(f"bias {b0['SigmaLow']:g}-{b0['SigmaHigh']}", lambda r: r["Statistics"]["Bands"][i]["BiasMedian"])
for i, b0 in enumerate(sb[:3]):
    per(f"leftover {b0['SigmaLow']:g}-{b0['SigmaHigh']}, %", lambda r: r["Statistics"]["Bands"][i]["Leftover"] / max(r["Statistics"]["Bands"][i]["Found"], 1), 100)

print("inpaint fraction, the masters that grew most (first -> second; dug cores refilled of the subtracted):")
for j in np.argsort(-idd)[:8]:
    s = new[names[j]]
    print(f"  {ia[j]:6.2f} -> {ib[j]:6.2f} %   {s['Statistics'].get('DugCores', 0):>7,} of {s['Subtracted']:>7,}   {names[j][:95]}")
print(f"  masters whose inpaint fraction grew by more than 5 points: {(idd > 5).sum()}, by more than 10: {(idd > 10).sum()}; "
      f"over 25 % inpainted: {(ia > 25).sum()} -> {(ib > 25).sum()}")
