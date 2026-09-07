"""E2.9: FWHM against air mass over the archive, from the per-sub PSF store.

Reads stats/psf-sessions.jsonl (last record per session wins, as DatasetPsfStore reads it) and, for
every session whose record carries the per-sub identity (SubAirmass beside SubFwhm, written by
`tianwen dataset build --remeasure-subs` or by any build since 2026-09-07), fits

    log(FWHM) = a + b * log(airmass)

by least squares over the subs with a finite air mass. Kolmogorov seeing predicts b = 0.6 for the
atmospheric part; the instrument's own blur and the pixel sampling floor pull it down, so a
short-focal train reads flatter than a long one. Per session the table gives the slope, the air-mass
span, the FWHM span the slope EXPLAINS over that span (span^b), and the FWHM span actually observed
(p90/p10). Pooled by optical train: how many sessions reach a 1.3x explained span, which is the
light end of the range E1 says matters (1.1x to 2x), and the kill line for airmass pairing.

The observed p90/p10 is NOT attributable to air mass: focus drift, wind and cloud all widen a sub,
and only the fitted slope times the span is. That is why both columns are printed.

Usage:
    python tools/psf-airmass-report.py D:/Astro-Dataset/2026-09-full [--min-subs 8] [--png out.png]

Text only by default; --png draws a log-log scatter with PIL when it is installed (matplotlib is not
on this box and is not required).
"""
import argparse
import json
import math
import os
import sys
from collections import defaultdict


def read_store(path):
    """Last record per session id, in file order, skipping a torn tail."""
    records = {}
    with open(path, encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            try:
                r = json.loads(line)
            except json.JSONDecodeError:
                continue
            records[r["SessionId"]] = r
    return records


def finite_pairs(record):
    fwhm = record.get("SubFwhm") or []
    airmass = record.get("SubAirmass")
    if not airmass or len(airmass) != len(fwhm):
        return []
    out = []
    for f, a in zip(fwhm, airmass):
        if a is None or f is None:
            continue
        if isinstance(a, str) or isinstance(f, str):
            # System.Text.Json writes NaN as the string "NaN" under the NumberHandling the store uses.
            continue
        if math.isfinite(a) and math.isfinite(f) and a > 0 and f > 0:
            out.append((a, f))
    return out


def loglog_slope(pairs):
    xs = [math.log(a) for a, _ in pairs]
    ys = [math.log(f) for _, f in pairs]
    n = len(xs)
    mx, my = sum(xs) / n, sum(ys) / n
    sxx = sum((x - mx) ** 2 for x in xs)
    if sxx <= 0:
        return float("nan"), float("nan")
    sxy = sum((x - mx) * (y - my) for x, y in zip(xs, ys))
    b = sxy / sxx
    a = my - b * mx
    return a, b


def percentile(v, p):
    s = sorted(v)
    return s[min(len(s) - 1, max(0, int(round(p * (len(s) - 1)))))]


def train_of(record):
    return record.get("OpticalTrain") or "?"


def filter_of(record):
    parts = record["SessionId"].split("|")
    return parts[3] if len(parts) > 3 else ""


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("outdir", help="the dataset out-dir holding stats/psf-sessions.jsonl")
    p.add_argument("--min-subs", type=int, default=8,
                   help="sessions with fewer subs carrying an air mass are listed but not fitted")
    p.add_argument("--span-target", type=float, default=1.3,
                   help="the explained FWHM span a session must reach to count for pairing (E1: 1.1x-2x matters)")
    p.add_argument("--png", default=None, help="write a log-log scatter here (needs PIL)")
    args = p.parse_args()

    store = os.path.join(args.outdir, "stats", "psf-sessions.jsonl")
    records = read_store(store)
    print(f"store: {store}")
    print(f"sessions: {len(records)}; with per-sub identity: "
          f"{sum(1 for r in records.values() if r.get('SubAirmass'))}; "
          f"selection: {dict(_count(r.get('SubSelection') or 'registered (pre-identity)' for r in records.values() if r.get('SubAirmass')))}")
    print()

    rows = []
    unfitted = []
    for sid, r in sorted(records.items()):
        pairs = finite_pairs(r)
        header = r.get("SubHeaderAirmass") or []
        n_header = sum(1 for h in header if isinstance(h, (int, float)) and math.isfinite(h))
        if len(pairs) < args.min_subs:
            unfitted.append((sid, len(r.get("SubFwhm") or []), len(pairs), n_header))
            continue
        a, b = loglog_slope(pairs)
        am = [x for x, _ in pairs]
        fw = [y for _, y in pairs]
        am_span = max(am) / min(am)
        explained = am_span ** b if math.isfinite(b) else float("nan")
        observed = percentile(fw, 0.9) / percentile(fw, 0.1)
        # Header cross-check where both exist: median |computed - header|.
        diffs = []
        airmass = r.get("SubAirmass") or []
        for c, h in zip(airmass, header):
            if isinstance(c, (int, float)) and isinstance(h, (int, float)) and math.isfinite(c) and math.isfinite(h):
                diffs.append(abs(c - h))
        rows.append({
            "session": sid, "train": train_of(r), "filter": filter_of(r), "subs": len(pairs),
            "slope": b, "am_min": min(am), "am_max": max(am), "am_span": am_span,
            "explained": explained, "observed": observed,
            "fwhm_p50": percentile(fw, 0.5), "header_n": n_header,
            "header_absdiff_p50": percentile(diffs, 0.5) if diffs else float("nan"),
        })

    print(f"| session | train | filter | subs | slope b | airmass min-max | span | explained FWHM span | observed p90/p10 | FWHM p50 px | header AIRMASS n / abs diff p50 |")
    print(f"|---|---|---|---:|---:|---|---:|---:|---:|---:|---|")
    for row in sorted(rows, key=lambda x: (x["train"], x["session"])):
        print(f"| {row['session'].split('|')[0][:40]} | {row['train'][:28]} | {row['filter'][:14]} | {row['subs']} | "
              f"{row['slope']:+.2f} | {row['am_min']:.2f}-{row['am_max']:.2f} | {row['am_span']:.2f}x | "
              f"{row['explained']:.3f}x | {row['observed']:.3f}x | {row['fwhm_p50']:.2f} | "
              f"{row['header_n']} / {row['header_absdiff_p50']:.3f} |")
    print()

    by_train = defaultdict(list)
    for row in rows:
        by_train[row["train"]].append(row)
    print(f"| train | sessions fitted | slope p50 | slope in [0.3, 0.9] | airmass span p50 | sessions with explained span >= {args.span_target:.1f}x | observed >= {args.span_target:.1f}x |")
    print(f"|---|---:|---:|---:|---:|---:|---:|")
    total_reach = 0
    for train, trs in sorted(by_train.items()):
        slopes = [t["slope"] for t in trs if math.isfinite(t["slope"])]
        reach = sum(1 for t in trs if math.isfinite(t["explained"]) and t["explained"] >= args.span_target)
        observed_reach = sum(1 for t in trs if t["observed"] >= args.span_target)
        total_reach += reach
        print(f"| {train[:40]} | {len(trs)} | {percentile(slopes, 0.5):+.2f} | "
              f"{sum(1 for s in slopes if 0.3 <= s <= 0.9)}/{len(slopes)} | "
              f"{percentile([t['am_span'] for t in trs], 0.5):.2f}x | {reach} | {observed_reach} |")
    print()
    print(f"sessions reaching a {args.span_target:.1f}x FWHM span attributable to air mass: {total_reach} of {len(rows)} fitted "
          f"({len(unfitted)} not fitted: fewer than {args.min_subs} subs with a finite air mass)")
    if unfitted:
        print("not fitted:")
        for sid, n_sub, n_pairs, n_header in unfitted:
            print(f"  {sid.split('|')[0][:50]:50} subs {n_sub:4d}  with airmass {n_pairs:4d}  header AIRMASS {n_header:4d}")

    if args.png:
        _scatter(rows, records, args.png)


def _count(values):
    c = defaultdict(int)
    for v in values:
        c[v] += 1
    return c


def _scatter(rows, records, path):
    try:
        from PIL import Image, ImageDraw
    except ImportError:
        print("PIL not installed; no PNG written", file=sys.stderr)
        return
    w, h, pad = 1000, 700, 60
    img = Image.new("RGB", (w, h), "white")
    d = ImageDraw.Draw(img)
    fitted = {r["session"] for r in rows}
    pts = []
    for sid, r in records.items():
        if sid not in fitted:
            continue
        for a, f in finite_pairs(r):
            pts.append((math.log10(a), math.log10(f), train_of(r)))
    if not pts:
        return
    xs = [x for x, _, _ in pts]
    ys = [y for _, y, _ in pts]
    x0, x1 = min(xs), max(xs) + 1e-9
    y0, y1 = min(ys), max(ys) + 1e-9
    trains = sorted({t for _, _, t in pts})
    palette = [(200, 30, 30), (30, 120, 200), (30, 160, 60), (200, 120, 0), (120, 40, 160), (0, 150, 150), (90, 90, 90)]
    colour = {t: palette[i % len(palette)] for i, t in enumerate(trains)}

    def px(x, y):
        return (pad + (x - x0) / (x1 - x0) * (w - 2 * pad), h - pad - (y - y0) / (y1 - y0) * (h - 2 * pad))

    d.rectangle([pad, pad, w - pad, h - pad], outline="black")
    for x, y, t in pts:
        cx, cy = px(x, y)
        d.ellipse([cx - 2, cy - 2, cx + 2, cy + 2], fill=colour[t])
    d.text((pad, h - pad + 10), f"log10 airmass  [{10 ** x0:.2f} .. {10 ** x1:.2f}]", fill="black")
    d.text((5, pad - 20), f"log10 FWHM px  [{10 ** y0:.2f} .. {10 ** y1:.2f}]", fill="black")
    for i, t in enumerate(trains):
        d.text((w - pad - 300, pad + 5 + i * 14), t[:40], fill=colour[t])
    img.save(path)
    print(f"wrote {path}")


if __name__ == "__main__":
    main()
