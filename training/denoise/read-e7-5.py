"""Read E7.5's sweep: one row per crop, the safe pick interpolated, the ring target read there.

Parses the text blocks `run-e7-5.py` filed under `C:/temp/e2/e7-5/` and answers the two
pre-registered questions in that order.

The SAFE PICK is the largest kernel scale at which all four star clauses hold (width 1.15 or under,
stars 0.85 to 1.10, ring within 1 MAD of the input's null, skirt 0.90 or over). Each clause is a
monotone function of the scale over this ladder, so each contributes a bound and the pick is the
smallest upper bound, interpolated in log scale (the ladder is third-octave, so the ratio is the
natural coordinate). A crop whose clauses never all hold is reported as such rather than picked.

`R*` is `ring.self` at that pick, and the four candidate normalisers are applied in the
pre-registered order, the first meeting the line being the answer.
"""
import glob
import math
import os
import re
import sys

OUT = "C:/temp/e2/e7-5"
CLAUSES = dict(width=1.15, stars_lo=0.85, stars_hi=1.10, ring=1.0, skirt=0.90)


def parse(path):
    text = open(path, encoding="utf-8").read()
    head = re.search(r"### (\w+) crop (\d+),(\d+),(\d+) kernel x([\d.]+)", text)
    if head is None:
        return None
    frame, cx, cy, size, scale = (head.group(1), int(head.group(2)), int(head.group(3)),
                                  int(head.group(4)), float(head.group(5)))
    truth = re.search(r"truth \(sharp\) (\d+) stars at 12 MAD, width ([\d.]+) px", text)
    field = re.search(r"field \(the INPUT's own[^)]*\): (\d+) detections at 12 MAD over (\d+) px square "
                      r"\(([\d.]+) per 1e5 px\), width ([\d.]+) px, median ([-\d.]+), MAD ([-\d.]+), "
                      r"ring null ([-\d.]+) MAD", text)
    masks = re.search(r"source masks from \S+: star ([\d.]+) of the crop, structure \(stars out\) ([\d.]+)", text)
    arm = next((ln for ln in text.splitlines() if ln.startswith("E3.1 ")), None)
    if truth is None or field is None or arm is None:
        return None
    v = arm[28:].split()
    return dict(frame=frame, cx=cx, cy=cy, size=size, scale=scale,
                n_truth=int(truth.group(1)), w_truth=float(truth.group(2)),
                n_in=int(field.group(1)), density=float(field.group(3)), w_in=float(field.group(4)),
                median=float(field.group(5)), mad=float(field.group(6)),
                null_self=float(field.group(7)),
                star_f=float(masks.group(1)) if masks else float("nan"),
                struct_f=float(masks.group(2)) if masks else float("nan"),
                width=float(v[0]), stars=float(v[1]), ring=float(v[2]), skirt=float(v[3]),
                # ring.self is the LAST statistic before the three per-channel widths, and how many
                # statistics precede it depends on whether the source masks added the two structure
                # columns, so it is counted from the end.
                ring_self=float(v[-4]))


def cross(rows, key, limit, falling):
    """The log-scale at which `key` crosses `limit`, by linear interpolation between ladder points.

    `falling` says which side is inside the clause. Returns (bound, kind) where kind is 'upper' or
    'lower', or None when the clause holds over the whole ladder."""
    xs = [math.log(r["scale"]) for r in rows]
    ys = [r[key] for r in rows]
    inside = [(y <= limit) if falling else (y >= limit) for y in ys]
    if all(inside):
        return None
    if not any(inside):
        # The clause holds nowhere on the ladder. That is not a bound, it is a crop the rule cannot
        # serve at any kernel, and reporting it as a bound at the ladder's end would hide exactly
        # the case worth seeing.
        return "never", None
    for i in range(len(rows) - 1):
        if inside[i] != inside[i + 1]:
            t = (limit - ys[i]) / (ys[i + 1] - ys[i]) if ys[i + 1] != ys[i] else 0.0
            x = xs[i] + t * (xs[i + 1] - xs[i])
            # inside below the crossing means the crossing is an UPPER bound on the scale
            return x, ("upper" if inside[i] else "lower")
    return (xs[0], "upper") if not inside[0] else (xs[-1], "lower")


def interp(rows, key, x):
    xs = [math.log(r["scale"]) for r in rows]
    ys = [r[key] for r in rows]
    if x <= xs[0]:
        return ys[0]
    for i in range(len(rows) - 1):
        if xs[i] <= x <= xs[i + 1]:
            t = (x - xs[i]) / (xs[i + 1] - xs[i])
            return ys[i] + t * (ys[i + 1] - ys[i])
    return ys[-1]


def pick(rows):
    """(scale, note) of the largest scale meeting every clause, or (None, why not)."""
    lo, hi = math.log(rows[0]["scale"]), math.log(rows[-1]["scale"])
    bounds = []
    for key, limit, falling in (("width", CLAUSES["width"], True),
                                ("stars", CLAUSES["stars_hi"], True),
                                ("skirt", CLAUSES["skirt"], False),
                                ("ring", CLAUSES["ring"], True)):
        c = cross(rows, key, limit, falling)
        if c is None:
            continue
        x, kind = c
        if x == "never":
            return None, f"{key} never meets {limit} anywhere on the ladder"
        bounds.append((key, x, kind))
    for key, x, kind in bounds:
        if kind == "upper":
            hi = min(hi, x)
        else:
            lo = max(lo, x)
    # the stars clause has a floor as well as a ceiling
    c = cross(rows, "stars", CLAUSES["stars_lo"], False)
    if c is not None and c[0] == "never":
        return None, "stars never reach 0.85 anywhere on the ladder"
    if c is not None and c[1] == "lower":
        lo = max(lo, c[0])
    if hi < lo:
        binding = ", ".join(f"{k} {kind} at x{math.exp(x):.2f}" for k, x, kind in bounds)
        return None, f"no scale meets every clause ({binding})"
    binding = min(((k, x) for k, x, kind in bounds if kind == "upper" and abs(x - hi) < 1e-9),
                  key=lambda kv: kv[1], default=("ladder end", hi))[0]
    return math.exp(hi), binding


def solve(rows, target):
    """The scale at which `ring.self` reaches `target`, by interpolation, or the ladder end it ran
    off. `ring.self` rises with the scale on every crop measured, which is E7.4's monotonicity."""
    xs = [math.log(r["scale"]) for r in rows]
    ys = [r["ring_self"] for r in rows]
    if target <= ys[0]:
        return xs[0], "below the ladder"
    if target >= ys[-1]:
        return xs[-1], "above the ladder"
    for i in range(len(rows) - 1):
        if ys[i] <= target <= ys[i + 1]:
            t = (target - ys[i]) / (ys[i + 1] - ys[i]) if ys[i + 1] != ys[i] else 0.0
            return xs[i] + t * (xs[i + 1] - xs[i]), ""
    return xs[-1], "not monotonic"


def clauses_at(rows, x):
    """Which of the four star clauses fail at log-scale `x`."""
    bad = []
    if interp(rows, "width", x) > CLAUSES["width"]:
        bad.append("width")
    s = interp(rows, "stars", x)
    if not (CLAUSES["stars_lo"] <= s <= CLAUSES["stars_hi"]):
        bad.append("stars")
    if abs(interp(rows, "ring", x)) > CLAUSES["ring"]:
        bad.append("ring")
    if interp(rows, "skirt", x) < CLAUSES["skirt"]:
        bad.append("skirt")
    return bad


def best_linear(crops, zkey, logz=False):
    """The most generous form of one declared normaliser: target = a + b*z, both fitted by search.

    A normaliser is being given two free parameters here rather than one, so a kill at this strength
    is a kill at every weaker form of the same candidate."""
    rows = [(k, v) for k, v in crops.items() if len(v) == 7]
    zs = [math.log(v[0][zkey]) if logz else v[0][zkey] for _, v in rows]
    span = (max(zs) - min(zs)) or 1.0
    best = (0, 0.0, 0.0, {})
    for bi in range(-40, 41):
        b = bi * 3.0 / 40 / span                       # b sweeps a full 3 MAD of tilt over the range
        for ai in range(-60, 31):
            a = ai / 20.0
            ok, fails = 0, {}
            for (_, v), z in zip(rows, zs):
                x, _ = solve(v, a + b * z)
                bad = clauses_at(v, x)
                if bad:
                    for t in bad:
                        fails[t] = fails.get(t, 0) + 1
                else:
                    ok += 1
            if ok > best[0]:
                best = (ok, a, b, fails)
    return best


def served(crops, targets, normalise=None):
    """How many crops a single target serves, over a range of targets."""
    out = []
    for t in targets:
        ok, fails = 0, {}
        for key, rows in crops.items():
            if len(rows) != 7:
                continue
            tgt = t * rows[0]["null_self"] if normalise == "null" else t
            x, _ = solve(rows, tgt)
            bad = clauses_at(rows, x)
            if bad:
                for b in bad:
                    fails[b] = fails.get(b, 0) + 1
            else:
                ok += 1
        out.append((t, ok, fails))
    return out


def main():
    # The skirt clause is the one most of the failures land on, so its threshold is the one worth
    # showing the kill is not an artefact of. E7.4 set it at 0.90 and it stays there; this only
    # re-runs the count at a looser one.
    for i, a in enumerate(sys.argv):
        if a == "--skirt":
            CLAUSES["skirt"] = float(sys.argv[i + 1])
            print(f"[skirt clause relaxed to {CLAUSES['skirt']}]")
    files = sorted(glob.glob(os.path.join(OUT, "*.txt")))
    recs = [r for r in (parse(f) for f in files) if r is not None]
    if not recs:
        print("no readable runs yet")
        return 1
    crops = {}
    for r in recs:
        crops.setdefault((r["frame"], r["cx"], r["cy"], r["size"]), []).append(r)
    for k in crops:
        crops[k].sort(key=lambda r: r["scale"])

    print(f"{len(recs)} runs over {len(crops)} crops\n")
    print(f"{'crop':24s} {'det/1e5':>8} {'w.in':>5} {'w.true':>6} {'blur':>5} {'MAD':>8} "
          f"{'null':>7} {'struct':>6} {'pick':>6} {'kernel':>7} {'R*':>7}  binds")
    table = []
    for (frame, cx, cy, size), rows in sorted(crops.items()):
        f = rows[0]
        # A partial ladder is not a short ladder: every clause here is read as a crossing, and a
        # crop whose scales stop early answers "never" to whatever the missing end would have met.
        s, note = (pick(rows) if len(rows) == 7
                   else (None, f"only {len(rows)} of 7 scales filed, not read"))
        base = 0.77 if frame == "statue" else 1.21          # channel 0's base, one number per frame
        blur = f["w_in"] / f["w_truth"]
        name = f"{frame[:3]}-{cx}-{cy}-{size}"
        lead = (f"{name:24s} {f['density']:8.1f} {f['w_in']:5.2f} {f['w_truth']:6.2f} {blur:5.3f} "
                f"{f['mad']:8.5f} {f['null_self']:7.2f} {f['struct_f']:6.3f}")
        if s is None:
            print(f"{lead}  {'':20s} {note}")
            continue
        rstar = interp(rows, "ring_self", math.log(s))
        table.append(dict(frame=frame, cx=cx, cy=cy, size=size, pick=s, kernel=base * s,
                          rstar=rstar, blur=blur, **{k: f[k] for k in
                                                     ("density", "w_in", "w_truth", "mad", "median",
                                                      "null_self", "struct_f", "n_in")}))
        print(f"{lead} {s:6.2f} {base * s:7.3f} {rstar:+7.2f}  {note}")

    field = [t for t in table if t["size"] == 512]
    if len(field) > 1:
        print("\n--- Q1: does the offset normalise? (field crops only, the 1024 control excluded)")
        raw = [t["rstar"] for t in field]
        spread = max(raw) - min(raw)
        print(f"raw R* spread {spread:.2f} MAD over {len(field)} crops "
              f"({min(raw):+.2f} to {max(raw):+.2f})")
        for name, fn in (("R* / null.self", lambda t: t["rstar"] / t["null_self"]),
                         ("R* vs log density", lambda t: t["rstar"]),
                         ("R* / MAD", lambda t: t["rstar"] / t["mad"]),
                         ("R* vs struct", lambda t: t["rstar"])):
            vals = [fn(t) for t in field]
            rel = (max(vals) - min(vals)) / (abs(sum(vals) / len(vals)) or 1.0)
            print(f"  {name:20s} spread {max(vals) - min(vals):8.3f}  relative {rel:6.2f}")
        for name, key in (("log density", "density"), ("MAD", "mad"), ("struct", "struct_f"),
                          ("null.self", "null_self"), ("w.in", "w_in"), ("blur w.in/w.true", "blur")):
            xs = [math.log(t[key]) if name == "log density" else t[key] for t in field]
            ys = raw
            n = len(xs)
            mx, my = sum(xs) / n, sum(ys) / n
            sx = math.sqrt(sum((x - mx) ** 2 for x in xs)) or 1e-12
            sy = math.sqrt(sum((y - my) ** 2 for y in ys)) or 1e-12
            r = sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / (sx * sy)
            print(f"  r(R*, {name:12s}) = {r:+.3f}")

    # E7.4's monotonicity is what makes a bisection possible at all, and it was read on two crops.
    # Whether it survives fourteen is a precondition for everything below, so it is checked first.
    bad = []
    for (frame, cx, cy, size), rows in sorted(crops.items()):
        if len(rows) != 7:
            continue
        ys = [r["ring_self"] for r in rows]
        drops = [(rows[i]["scale"], ys[i + 1] - ys[i]) for i in range(6) if ys[i + 1] < ys[i]]
        if drops:
            bad.append((f"{frame[:3]}-{cx}-{cy}-{size}", drops))
    print(f"\nring.self monotonic in the kernel on "
          f"{len([k for k, v in crops.items() if len(v) == 7]) - len(bad)} of "
          f"{len([k for k, v in crops.items() if len(v) == 7])} crops read")
    for name, drops in bad:
        print("  " + name + ": falls at " + ", ".join(f"x{s:.2f} by {d:+.2f}" for s, d in drops))

    full = {k: v for k, v in crops.items() if len(v) == 7 and k[3] == 512}
    if full:
        print(f"\n--- Q1, the half that decides: how many of the {len(full)} field crops does ONE "
              f"target serve?")
        print("  a flat target in ring.self units")
        for t, ok, fails in served(full, [-2.0, -1.5, -1.0, -0.8, -0.6, -0.4, -0.2, 0.0, 0.3]):
            why = ", ".join(f"{k} {n}" for k, n in sorted(fails.items())) or "none fail"
            print(f"    target {t:+5.2f}: {ok:2d}/{len(full)} served   ({why})")
        print("  the first declared normaliser, a target in units of the input's own ring null")
        for t, ok, fails in served(full, [-0.4, -0.3, -0.2, -0.15, -0.1, -0.05, 0.0, 0.1],
                                   normalise="null"):
            why = ", ".join(f"{k} {n}" for k, n in sorted(fails.items())) or "none fail"
            print(f"    target {t:+5.2f} x null: {ok:2d}/{len(full)} served   ({why})")
        print("  every declared normaliser at its most generous, target = a + b*z, both fitted:")
        for name, key, lg in (("(flat, b forced 0)", "null_self", False),
                              ("1 the input's ring null", "null_self", False),
                              ("2 log detection density", "density", True),
                              ("3 the input's noise MAD", "mad", False),
                              ("4 the structure fraction", "struct_f", False)):
            if name.startswith("(flat"):
                ok = max(o for _, o, _ in served(full, [x / 20.0 for x in range(-60, 21)]))
                print(f"    {name:26s} {ok:2d}/{len(full)}")
                continue
            ok, a, b, fails = best_linear(full, key, lg)
            why = ", ".join(f"{k} {n}" for k, n in sorted(fails.items())) or "none fail"
            print(f"    {name:26s} {ok:2d}/{len(full)}  at a {a:+.2f} b {b:+.4g}   ({why})")

    grid = [t for t in table if t["frame"] == "statue" and t["size"] == 512]
    if len(grid) > 1:
        print("\n--- Q2: what window does the field term want? (Statue 3x3, absolute kernel px)")
        ks = {(t["cx"], t["cy"]): t["kernel"] for t in grid}
        for cy in sorted({c[1] for c in ks}):
            print("  " + "  ".join(f"{ks.get((cx, cy), float('nan')):6.3f}"
                                   for cx in sorted({c[0] for c in ks})))
        vals = list(ks.values())
        print(f"  spread {max(vals) / min(vals):.2f}x ({min(vals):.3f} to {max(vals):.3f} px)")
        adj = []
        for (cx, cy), k in ks.items():
            for dx, dy in ((1256, 0), (0, 1182)):
                other = ks.get((cx + dx, cy + dy))
                if other:
                    adj.append(max(k, other) / min(k, other))
        if adj:
            print(f"  adjacent cells differ {min(adj):.2f}x to {max(adj):.2f}x "
                  f"(median {sorted(adj)[len(adj) // 2]:.2f}x) over {len(adj)} pairs")
        # Does the pick track the blur that is actually there? The picks are truth-free; w.in/w.true
        # is not, and is the only thing that says whether a field term is being followed or invented.
        # Is the clause-based pick finding the kernel that is actually missing? Gaussian composition
        # says the blur between two widths is their difference in quadrature; where that is imaginary
        # there is no blur to remove at all. This uses the truth and is therefore a check on the PICK,
        # never a rule: nothing at inference knows w.true.
        print("  the pick against the kernel the two widths imply (quadrature, uses the truth):")
        for t in field:
            q2 = t["w_in"] ** 2 - t["w_truth"] ** 2
            t["k_quad"] = math.sqrt(q2) if q2 > 0 else 0.0
            # The fraction rule is what E7.4 named as each bisection's starting point: 0.52 times
            # the window's own width. It is truth-free, so it is also the one candidate rule that
            # could stand in for a target, and this is where it is read against the answer.
            frac = 0.52 * t["w_in"]
            print(f"    {t['frame'][:3]}-{t['cx']}-{t['cy']:<5d} implied "
                  f"{t['k_quad']:5.3f} px   picked {t['kernel']:5.3f} px"
                  + ("   (no blur present)" if t["k_quad"] == 0.0 else
                     f"   {t['kernel'] / t['k_quad']:5.2f}x")
                  + f"   fraction rule {frac:5.3f} px"
                  + ("" if t["k_quad"] == 0.0 else f" ({frac / t['k_quad']:5.2f}x)"))
        have = [t for t in field if t["k_quad"] > 0]
        if len(have) > 1:
            xs = [t["k_quad"] for t in have]
            ys = [t["kernel"] for t in have]
            n = len(xs)
            mx, my = sum(xs) / n, sum(ys) / n
            sx = math.sqrt(sum((x - mx) ** 2 for x in xs)) or 1e-12
            sy = math.sqrt(sum((y - my) ** 2 for y in ys)) or 1e-12
            print(f"    r(picked, implied) = "
                  f"{sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / (sx * sy):+.3f} "
                  f"over the {len(have)} crops that carry blur")
        print("  the pick against what is actually there (all field crops, both frames):")
        for name, key in (("blur w.in/w.true", "blur"), ("w.in", "w_in"), ("w.true", "w_truth"),
                          ("log density", "density"), ("MAD", "mad"), ("null.self", "null_self")):
            xs = [math.log(t[key]) if name == "log density" else t[key] for t in field]
            ys = [t["kernel"] for t in field]
            n = len(xs)
            mx, my = sum(xs) / n, sum(ys) / n
            sx = math.sqrt(sum((x - mx) ** 2 for x in xs)) or 1e-12
            sy = math.sqrt(sum((y - my) ** 2 for y in ys)) or 1e-12
            print(f"    r(kernel, {name:16s}) = "
                  f"{sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / (sx * sy):+.3f}")
        for t in table:
            if t["size"] == 1024:
                near = min(grid, key=lambda g: (g["cx"] - t["cx"]) ** 2 + (g["cy"] - t["cy"]) ** 2)
                print(f"  size control: 1024 at ({t['cx']},{t['cy']}) picks {t['kernel']:.3f} px; "
                      f"512 at ({near['cx']},{near['cy']}) picks {near['kernel']:.3f} px "
                      f"({max(t['kernel'], near['kernel']) / min(t['kernel'], near['kernel']):.2f}x)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
