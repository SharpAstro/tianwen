"""E16c step 1's read: docs/plans/denoiser-training.md, "E16c step 1, the level estimator, pre-registered".

Usage: python e16c_read.py <noise-check --table output> <bake root>

The split, the k rule and the predictions are the plan's; this script only applies them to `tianwen dataset noise-check
--table`'s per-session table, whose cells are each session's quiet-cell median per channel of measured over predicted
(1 a match, under 1 an over-read). fineScaleRaw is the candidate at k = 1, so its reading at k is fineScaleRaw * k.
"""
import csv
import hashlib
import math
import statistics
import sys

HARD_MONO = ("ZWO-ASI1600MM-Pro/Ha/eta-Car-Nebula/2025-02-06", "ZWO-ASI294MM/Luminance/Lagoon-and-Trifid/2022-06-28")
DENSE_STARS = ("Uranus-C-IMX585/Baader-Semi-APO/Lagoon-and-Trifid/2023-08-03", "Large-Magellanic-Cloud",
               "Sagittarius-Star-Cloud", "Prawn-Nebula")


def read_test(bake):
    test = set()
    with open(f"{bake}/test-sessions.txt", encoding="utf-8") as f:
        for line in f:
            line = line.split("\t", 1)[0].strip()
            if line and not line.startswith("#"):
                test.add(line)
    return test


def base(session):
    return session.split("|flip=", 1)[0]


def number(row, name, c):
    text = row.get(f"{name}{c}", "")
    try:
        return float(text)
    except ValueError:
        return math.nan


def typical(ratios):
    return math.exp(statistics.fmean(abs(math.log(r)) for r in ratios)) if ratios else math.nan


def main():
    table, bake = sys.argv[1], sys.argv[2]
    test = read_test(bake)

    def tuning(session):
        b = base(session)
        return b not in test and hashlib.sha256(b.encode("utf-8")).digest()[0] % 2 == 0

    with open(table, encoding="utf-8") as f:
        rows = list(csv.DictReader(f, delimiter="\t"))
    channels_of = {r["session"]: int(r["channels"]) for r in rows}

    # Parity: the shipped estimator re-run on the whole master against the recorded anchor.
    worst = 0.0
    for r in rows:
        for c in range(channels_of[r["session"]]):
            m, b = number(r, "masterCalibration", c), number(r, "blocks", c)
            if math.isfinite(m) and math.isfinite(b) and m > 0:
                worst = max(worst, abs(b / m - 1.0))
    parity = worst <= 0.01
    print(f"parity: blocks against the recorded master anchor, worst {100 * worst:.2f} percent -> {'PASS' if parity else 'FAIL'}")
    if not parity:
        print("nothing below is read (the plan: the recorded anchors are not what the code gives)")

    # k per (integration, channel) from the tuning split alone.
    pools = {}
    for r in rows:
        if not tuning(r["session"]):
            continue
        for c in range(channels_of[r["session"]]):
            k = number(r, "pairFineScaleRatio", c)
            if math.isfinite(k) and k > 0:
                pools.setdefault((r["integration"], c), []).append(k)
    k_of = {}
    print("\nk per integration and channel (tuning split), prediction 1: sd of ln at most 0.05")
    p1 = True
    for key in sorted(pools):
        ks = pools[key]
        k_of[key] = statistics.median(ks)
        spread = statistics.stdev(math.log(k) for k in ks) if len(ks) > 1 else math.nan
        holds = len(ks) > 1 and spread <= 0.05
        p1 &= holds
        print(f"  {key[0]:10s} channel {key[1]}: k {k_of[key]:.4f} over {len(ks)} sessions, sd ln {spread:.4f} {'holds' if holds else 'MISSES'}")

    def fine(r, c):
        k = k_of.get((r["integration"], c))
        raw = number(r, "fineScaleRaw", c)
        return raw * k if k is not None and math.isfinite(raw) else math.nan

    def readings(selected):
        out = []
        for r in selected:
            for c in range(channels_of[r["session"]]):
                b, f = number(r, "masterCalibration", c), fine(r, c)
                if math.isfinite(b) and math.isfinite(f) and b > 0 and f > 0:
                    out.append((r["session"], c, b, f))
        return out

    validation = readings([r for r in rows if not tuning(r["session"])])
    within = lambda xs: sum(1 for x in xs if 0.90 <= x <= 1.10)
    under = lambda xs: sum(1 for x in xs if x < 0.90)
    base_ratios = [b for _, _, b, _ in validation]
    fine_ratios = [f for _, _, _, f in validation]
    n = len(validation)
    print(f"\nvalidation: {n} session-channels over {len({s for s, *_ in validation})} sessions")
    share = within(fine_ratios) / n if n else math.nan
    print(f"prediction 2, within 10 percent on at least 75 percent: candidate {within(fine_ratios)} of {n} ({100 * share:.1f}%), "
          f"baseline {within(base_ratios)} -> {'holds' if share >= 0.75 else 'MISSES'}")
    print(f"prediction 3, typical error at most x1.12: candidate x{typical(fine_ratios):.3f}, baseline x{typical(base_ratios):.3f} "
          f"-> {'holds' if typical(fine_ratios) <= 1.12 else 'MISSES'}")

    every = readings(rows)
    print("\nprediction 4, the hard mono nights within 15 percent (either split):")
    for night in HARD_MONO:
        for s, c, b, f in every:
            if night in s:
                print(f"  {s.split('|')[0]} ch{c}: baseline {b:.3f}, candidate {f:.3f} -> {'holds' if 0.85 <= f <= 1.15 else 'MISSES'}")
    print("prediction 5, dense star fields at least 0.80 (either split):")
    for s, c, b, f in every:
        if any(d in s for d in DENSE_STARS):
            print(f"  {s.split('|')[0]} ch{c}: baseline {b:.3f}, candidate {f:.3f} -> {'holds' if f >= 0.80 else 'MISSES'}")

    kill = typical(fine_ratios) >= typical(base_ratios) or under(fine_ratios) > under(base_ratios)
    print(f"\nKILL: typical error {typical(fine_ratios):.3f} against {typical(base_ratios):.3f}; under 0.90 on {under(fine_ratios)} "
          f"against {under(base_ratios)} -> {'FIRES' if kill else 'does not fire'}")

    print("\nper session-channel (validation): baseline, candidate")
    for s, c, b, f in sorted(validation, key=lambda t: t[3]):
        print(f"  {f:6.3f}  {b:6.3f}  ch{c}  {s.split('|')[0]}{' (flip ' + s.split('|flip=')[1] + ')' if '|flip=' in s else ''}")


if __name__ == "__main__":
    main()
