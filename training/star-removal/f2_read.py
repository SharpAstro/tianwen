"""F2's read (#1827): each model's starless-eval-v5.json on the master-stretched sets beside its plate-stretched one, as a table.

For A's draws and each twin, one block of measures with, per model, the plate-stretched number and the master-stretched number, and
the spread of two seeds of one recipe on the master-stretched set (the bar the plan states). The plate-stretched A was read on 8
sessions and F2's on 7 (the Orion night is absent from every new set), so where the old report holds a per-session breakdown the
old A's column is pooled over the same 7 sessions; else it is the 8-session figure and the line says so.

    python -I f2_read.py --old C:/temp/e2/r2-eval --new C:/temp/tianwen-scratch/f2-eval-master --models r2i_synth4_s0 r2i_synth4_s1 r2t_synth4_s1 r2w_synth11_s0 --seeds r2i_synth4_s0 r2i_synth4_s1
"""
import argparse
import json
import math
import os

ap = argparse.ArgumentParser()
ap.add_argument("--old", required=True, help="the plate-stretched eval root (<model>/on-random, on-twin-plain, on-twin-lobed)")
ap.add_argument("--new", required=True, help="the master-stretched eval root, the same layout")
ap.add_argument("--models", nargs="+", required=True)
ap.add_argument("--seeds", nargs=2, required=True, help="the two models of one recipe whose spread is the bar")
ap.add_argument("--drop-session", default="Great-Orion-Nebula", help="the session text the new sets lack; the old A is pooled without it")
ap.add_argument("--report", default="starless-eval-v5.json")
ap.add_argument("--out", default="", help="write the table here as well")
args = ap.parse_args()
BANDS = ("5-20 sigma", "20-100 sigma", "100-1000 sigma", "saturated")


def load(root, model, folder):
    p = f"{root}/{model}/{folder}/{args.report}"
    return json.load(open(p, encoding="utf-8")) if os.path.exists(p) else None


def output_arm(arms):
    return next(a for a in arms if a["Name"] == "output")


def band(arm, name):
    return next((c for c in arm["Completeness"] if c["Band"] == name), None)


def colour_band(arm, name):
    return next((c for c in arm["Colour"]["Completeness"] if c["Band"] == name), None)


def pooled(arms_by_session, field, name, colour=False):
    """Stars-weighted pool of a band's rate over sessions' output arms."""
    num = den = 0.0
    for arm in arms_by_session:
        b = (colour_band if colour else band)(arm, name)
        if b is None or not b["Stars"]:
            continue
        num += b[field] * b["Stars"]
        den += b["Stars"]
    return num / den if den else float("nan")


def pooled_core(arms_by_session, name):
    s1 = s2 = n = 0.0
    for arm in arms_by_session:
        b = band(arm, name)
        if b is None or not b["Stars"]:
            continue
        s1 += b["CoreMean"] * b["Stars"]
        s2 += (b["CoreRms"] ** 2) * b["Stars"]
        n += b["Stars"]
    return (s1 / n, math.sqrt(s2 / n)) if n else (float("nan"), float("nan"))


def measures(report, drop=None):
    """(name -> value) for one report; with drop, pooled over the sessions that lack that text, else the report's own pool."""
    if drop and report.get("Sessions"):
        arms = [output_arm(s["Arms"]) for s in report["Sessions"] if drop not in s["SessionId"]]
        note = "7"
    else:
        arms = [output_arm(report["Arms"])]
        note = "8" if drop else ""
    m = {}
    for b, label in zip(BANDS[:3], ("5-20", "20-100", "100-1000")):
        m[f"clean {label}"] = 100 * pooled(arms, "CleanRate", b)
    m["dug 100-1000"] = 100 * pooled(arms, "DugRate", "100-1000 sigma")
    m["dug 20-100"] = 100 * pooled(arms, "DugRate", "20-100 sigma")
    m["dug 5-20"] = 100 * pooled(arms, "DugRate", "5-20 sigma")
    cm, cr = pooled_core(arms, "100-1000 sigma")
    m["core mean 100-1000"], m["core rms 100-1000"] = cm, cr
    m["clean saturated"] = 100 * pooled(arms, "CleanRate", "saturated")
    m["dug saturated"] = 100 * pooled(arms, "DugRate", "saturated")
    for b, label in zip(BANDS, ("5-20", "20-100", "100-1000", "saturated")):
        m[f"colour clean {label}"] = 100 * pooled(arms, "CleanRate", b, colour=True)
    m["colour dug 100-1000"] = 100 * pooled(arms, "DugRate", "100-1000 sigma", colour=True)
    # The loss share and far sky are the report's own pool (no per-session breakdown of them is stored).
    o = output_arm(report["Arms"])
    m["bright-core share %"] = 100 * o["Loss"]["BrightCores"]
    m["far sky"] = o["PlateSources"]["FarRms"]
    return m, note


lines = []


def emit(text=""):
    lines.append(text)
    print(text)


SETS = (("A's draws", "on-random"), ("plain twin", "on-twin-plain"), ("lobed twin", "on-twin-lobed"))
for title, folder in SETS:
    cols = {}
    notes = set()
    for model in args.models:
        old, new = load(args.old, model, folder), load(args.new, model, folder)
        mo, note = measures(old, args.drop_session if folder == "on-random" else None) if old else (None, "")
        mn, _ = measures(new) if new else (None, "")
        cols[model] = (mo, mn)
        if note:
            notes.add(note)
    emit(f"== {title} ({folder}); per model: plate-stretched / master-stretched" +
         (f"; the old A pooled over {'/'.join(sorted(notes))} sessions" if notes else ""))
    emit(f"{'measure':22s}" + "".join(f"{m[:16]:>20s}" for m in args.models) + f"{'seed spread':>13s}")
    first = next(iter(cols.values()))[1] or next(iter(cols.values()))[0] or {}
    for key in first:
        row = f"{key:22s}"
        for model in args.models:
            mo, mn = cols[model]
            a = f"{mo[key]:.2f}" if mo and key in mo and mo[key] == mo[key] else "-"
            b = f"{mn[key]:.2f}" if mn and key in mn and mn[key] == mn[key] else "-"
            row += f"{a + ' / ' + b:>20s}"
        s0, s1 = (cols[m][1] for m in args.seeds)
        spread = f"{abs(s0[key] - s1[key]):.2f}" if s0 and s1 and key in s0 and key in s1 else "-"
        emit(row + f"{spread:>13s}")
    emit()
if args.out:
    with open(args.out, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(lines) + "\n")
