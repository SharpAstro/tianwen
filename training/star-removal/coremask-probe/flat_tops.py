"""The clipped-star rule's measures over every source of a survey (#2360).
    python flat_tops.py <survey.jsonl of a probe run with both star rules off> [min top px] [min roundness] [max fall] [max core over top]
The probe is run with --max-peak-ratio 1e9 --max-core-over-flat-top 0, so that every source is listed with its peak ratio
and its flat top's area, roundness and fall. A source at a peak ratio over 6 is a star by the star rule. One at 6 or under
was a kept core before the clipped-star rule, and the owner's read of those 77 (#2201) says all are real but one, the
Pleiades master of 2022-01-05, which is Alcyone. Printed: every source whose flat top is wide enough to read, by class,
and what the rule at the given bars (default: the library's) would turn away. The measures are the library's; this only
reads the probe's JSON."""
import json, sys
import statistics as st

SURVEY = sys.argv[1]
MIN_PX = int(sys.argv[2]) if len(sys.argv) > 2 else 50
MIN_ROUND = float(sys.argv[3]) if len(sys.argv) > 3 else 0.75
MAX_FALL = float(sys.argv[4]) if len(sys.argv) > 4 else 0.18
MAX_OVER = float(sys.argv[5]) if len(sys.argv) > 5 else 9.0
ALCYONE = "iTelescope-T68_No-Filter_Pleiades_2022-01-05_ASCOM_M45_Color"

rows = []
n_kept = 0
for r in sorted((json.loads(l) for l in open(SURVEY, encoding="utf-8")), key=lambda r: r["stem"]):
    for c in r["cores"]:
        kept = c["peakRatio"] <= 6
        if kept:
            n_kept += 1
        cls = "star" if not kept else "ALCYONE" if r["stem"] == ALCYONE else "core"
        rows.append(dict(cls=cls, n=n_kept if kept else 0, stem=r["stem"], ratio=c["peakRatio"], core=c["CoreAreaPx"], top=c["FlatTopPx"],
                         rnd=c["flatTopRoundness"], fall=c["flatTopFall"]))


def star_top(x):
    return x["top"] >= MIN_PX and x["rnd"] >= MIN_ROUND and x["fall"] <= MAX_FALL


def turned_away(x):
    return star_top(x) and x["core"] <= MAX_OVER * x["top"]


print(f"sources {len(rows)}: " + ", ".join(f"{k} {sum(x['cls'] == k for x in rows)}" for k in ("core", "ALCYONE", "star")))
print(f"bars: top >= {MIN_PX} px, roundness >= {MIN_ROUND}, fall <= {MAX_FALL}, core <= {MAX_OVER} x top")
for cls in ("ALCYONE", "core", "star"):
    wide = sorted((x for x in rows if x["cls"] == cls and x["top"] >= MIN_PX), key=lambda x: -x["top"])
    print(f"\n{cls}: {len(wide)} of {sum(x['cls'] == cls for x in rows)} with a top of {MIN_PX} px or over"
          f" ({sum(x['top'] == 0 for x in rows if x['cls'] == cls)} with none read)")
    print("  tile  ratio   core_px  top_px  round   fall   core/top  star_top  away  master")
    for x in wide:
        print(f"  {x['n'] or '':>4}  {x['ratio']:>5.2f}  {x['core']:>7}  {x['top']:>6}  {x['rnd']:>5.2f}  {x['fall']:>6.3f}  {x['core'] / x['top']:>7.1f}"
              f"  {'yes' if star_top(x) else 'no':>8}  {'AWAY' if turned_away(x) else '':>4}  {x['stem'][:60]}")
    if wide:
        print(f"  roundness min {min(x['rnd'] for x in wide):.2f} median {st.median(x['rnd'] for x in wide):.2f} max {max(x['rnd'] for x in wide):.2f};"
              f" fall min {min(x['fall'] for x in wide):.3f} median {st.median(x['fall'] for x in wide):.3f} max {max(x['fall'] for x in wide):.3f}")
away = [x for x in rows if x["cls"] != "star" and turned_away(x)]
print(f"\nkept cores the rule turns away: {len(away)}: " + "; ".join(f"{x['cls']} {x['n']} {x['stem'][:50]}" for x in away))
