"""E2.10: seeing-split pairs. Which sessions have enough FWHM spread to split, and the two manifests
that build a SHARP and a SOFT master of one session on the same reference.

Real-blur validation at the light end (docs/plans/deconvolver-training.md, E2.10): a session whose
per-sub FWHM spans p90/p10 >= 1.15 is split into its sharpest third (master A, the truth) and its
softest third (master B, the input), and a deconvolver is scored on B against A. Both masters must
share one reference frame and one canvas, or they do not overlay; `tianwen stack --manifest` is what
guarantees that, and a manifest pins frames by a digest of their pixels, so this script does not
WRITE a manifest from the PSF store. It FILTERS one:

  1. list the qualifying sessions (this script, no --manifest):
         python tools/psf-seeing-split.py D:/Astro-Dataset/2026-09-full
  2. stack the whole session once, which writes master_<slug>.manifest.json beside the master with
     every registered frame's transform and the reference it chose;
  3. split that manifest into the two thirds (this script, with --manifest and --session), which keeps
     the reference frame in BOTH halves because a manifest reference absent from the run's frames is a
     fatal error by design; it is one frame in about thirty and the script says so;
  4. `tianwen stack <lights> --manifest <slug>-sharp.manifest.json` and again with -soft.

The thirds are taken by rank on SubFwhm, the middle third is dropped, and subs whose FWHM the store
does not carry (a sub the gate rejected, or one that failed to register) are neither sharp nor soft.
Membership is matched on the FILE NAME, case-insensitively, because the store's SubFile and the
manifest's Path are both archive paths that may differ in drive letter or separator; a name that
appears twice in one session is reported and the split refuses, rather than guessing.
"""
import argparse
import json
import math
import os
import sys


def read_store(path):
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


def percentile(v, p):
    s = sorted(v)
    return s[min(len(s) - 1, max(0, int(round(p * (len(s) - 1)))))]


def finite(v):
    return isinstance(v, (int, float)) and math.isfinite(v)


def session_subs(record):
    """(file, fwhm) for every sub the record identifies, in store order."""
    files = record.get("SubFile")
    fwhm = record.get("SubFwhm") or []
    if not files or len(files) != len(fwhm):
        return []
    return [(f, w) for f, w in zip(files, fwhm) if f and finite(w) and w > 0]


def thirds(subs):
    """(sharp, soft) lists of (file, fwhm), each the outer third by FWHM rank."""
    ranked = sorted(subs, key=lambda t: t[1])
    k = len(ranked) // 3
    if k == 0:
        return [], []
    return ranked[:k], ranked[-k:]


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("outdir", help="the dataset out-dir holding stats/psf-sessions.jsonl")
    p.add_argument("--min-ratio", type=float, default=1.15, help="SubFwhm p90/p10 a session must reach")
    p.add_argument("--min-subs", type=int, default=15, help="fewest identified subs a session needs (a third of 15 is 5)")
    p.add_argument("--session", default=None, help="a substring of the SessionId to split (with --manifest)")
    p.add_argument("--manifest", default=None, help="the full-session master_<slug>.manifest.json to split")
    p.add_argument("--out-dir", default=None, help="where the -sharp and -soft manifests go (default: beside the input)")
    args = p.parse_args()

    store = os.path.join(args.outdir, "stats", "psf-sessions.jsonl")
    records = read_store(store)

    qualifying = []
    for sid, r in sorted(records.items()):
        subs = session_subs(r)
        if len(subs) < args.min_subs:
            continue
        widths = [w for _, w in subs]
        ratio = percentile(widths, 0.9) / percentile(widths, 0.1)
        sharp, soft = thirds(subs)
        a = percentile([w for _, w in sharp], 0.5)
        b = percentile([w for _, w in soft], 0.5)
        qualifying.append((sid, r.get("OpticalTrain", "?"), len(subs), ratio, a, b, r.get("SubSelection")))

    print(f"store: {store}")
    print(f"sessions: {len(records)}; with per-sub identity and >= {args.min_subs} subs: {len(qualifying)}")
    print()
    print(f"| session | train | subs | p90/p10 | sharp third p50 px | soft third p50 px | soft/sharp | qualifies (>= {args.min_ratio:.2f}) | subs are |")
    print("|---|---|---:|---:|---:|---:|---:|---|---|")
    n_q = 0
    for sid, train, n, ratio, a, b, sel in sorted(qualifying, key=lambda t: -t[3]):
        q = ratio >= args.min_ratio
        n_q += q
        print(f"| {sid.split('|')[0][:44]} | {train[:26]} | {n} | {ratio:.3f} | {a:.2f} | {b:.2f} | {b / a:.3f} | {'YES' if q else 'no'} | {sel or 'registered'} |")
    print()
    print(f"{n_q} session(s) reach p90/p10 >= {args.min_ratio:.2f}")

    if not args.manifest:
        return

    if not args.session:
        sys.exit("--manifest needs --session to say which record's widths to split on")
    matches = [sid for sid in records if args.session.lower() in sid.lower()]
    if len(matches) != 1:
        sys.exit(f"--session '{args.session}' matches {len(matches)} record(s): {matches[:5]}")
    record = records[matches[0]]
    subs = session_subs(record)
    if len(subs) < args.min_subs:
        sys.exit(f"{matches[0]} identifies only {len(subs)} subs")

    by_name = {}
    dupes = set()
    for f, w in subs:
        name = os.path.basename(f.replace("\\", "/")).lower()
        if name in by_name:
            dupes.add(name)
        by_name[name] = w
    if dupes:
        sys.exit(f"{len(dupes)} file name(s) appear twice in this session's record; a split by name would be ambiguous: {sorted(dupes)[:5]}")

    with open(args.manifest, encoding="utf-8") as fh:
        manifest = json.load(fh)
    frames = manifest["Frames"]
    matched = [fr for fr in frames if fr.get("Fate") == "Matched"]
    ref_name = os.path.basename(manifest["ReferencePath"].replace("\\", "/")).lower()

    # Widths for the manifest's own matched frames, from the store; frames the store does not know
    # are neither sharp nor soft and are reported.
    known = []
    unknown = []
    for fr in matched:
        name = os.path.basename(fr["Path"].replace("\\", "/")).lower()
        if name in by_name:
            known.append((fr, by_name[name]))
        else:
            unknown.append(fr["Path"])
    if len(known) < args.min_subs:
        sys.exit(f"only {len(known)} manifest frames have a width in the store ({len(unknown)} unknown)")

    ranked = sorted(known, key=lambda t: t[1])
    k = len(ranked) // 3
    sharp = ranked[:k]
    soft = ranked[-k:]

    def with_reference(third, label):
        names = {os.path.basename(fr["Path"].replace("\\", "/")).lower() for fr, _ in third}
        out = [fr for fr, _ in third]
        if ref_name not in names:
            ref = next((fr for fr in matched if os.path.basename(fr["Path"].replace("\\", "/")).lower() == ref_name), None)
            if ref is None:
                sys.exit(f"the manifest's reference {manifest['ReferencePath']} is not among its matched frames")
            out.append(ref)
            print(f"  {label}: reference frame added (it is not in this third by rank); {len(out)} frames, one shared with the other half")
        return out

    base = args.out_dir or os.path.dirname(os.path.abspath(args.manifest))
    stem = os.path.basename(args.manifest)
    stem = stem[:-len(".manifest.json")] if stem.endswith(".manifest.json") else os.path.splitext(stem)[0]
    for label, third in (("sharp", sharp), ("soft", soft)):
        out_frames = with_reference(third, label)
        out = dict(manifest)
        out["Frames"] = out_frames
        path = os.path.join(base, f"{stem}-{label}.manifest.json")
        with open(path, "w", encoding="utf-8") as fh:
            json.dump(out, fh, indent=2)
        widths = [w for _, w in third]
        print(f"  {label}: {len(third)} frames by rank, FWHM p50 {percentile(widths, 0.5):.2f} px "
              f"[{min(widths):.2f} .. {max(widths):.2f}] -> {path}")
    a = percentile([w for _, w in sharp], 0.5)
    b = percentile([w for _, w in soft], 0.5)
    print(f"  soft/sharp p50 ratio {b / a:.3f}; {len(unknown)} matched frame(s) without a stored width, dropped from both")


if __name__ == "__main__":
    main()
