# E16b's widening, by the rule the plan pre-registered: E16a's fourteen sessions gave fewer than 150 bright cells over
# three sessions, so the store's other sessions join, ranked by their bright-cell count, taking their bright cells only
# (up to 45 each), until both hold. Out: E16a's own train and val sessions, the store's test split, the eval fields'
# sessions, any session whose plate overlaps an eval field's (each plate taken as a circle of its half-diagonal, which
# excludes more than a rectangle would), any master with no position to check, and flip halves (the same sky as their
# session, so their bright cells would be the same cells twice).
#   python e16b_widen_select.py <bright-all.log> <bright-train14.txt> <fields.tsv> <test-sessions.txt> <train list> <val list> <eval val lists...>
import math, re, sys

log, train14_log, fields_path, test_path, train_path, val_path, *eval_paths = sys.argv[1:]
PER_SESSION, NEED_CELLS, NEED_SESSIONS = 45, 150, 3

def ids(path):
    return [l.split("\t")[0].strip() for l in open(path, encoding="utf-8") if l.strip() and not l.startswith("#")]

def slug(sid):
    return sid.replace("/", "_").replace("|", "_")

def read_counts(path, group):
    out = {}
    for line in open(path, encoding="utf-8", errors="replace"):
        m = re.match(r"\[bright-cells\]\s+(\d+) bright of\s+(\d+) cells,\s+(\d+) listed\s+(.*)$", line.rstrip())
        if m:
            out[m.group(4)] = int(m.group(group))
    return out

counts = read_counts(log, 1)
# E16a's own sessions as the arm takes them: their export's 120-cell sample left out, up to 45 listed.
e16a_listed = read_counts(train14_log, 3)

fields = {}
for line in open(fields_path, encoding="utf-8").readlines()[1:]:
    p = line.rstrip("\n").split("\t")
    try:
        ra, dec, w, h = map(float, p[1:5])
    except ValueError:
        continue
    if all(math.isfinite(v) for v in (ra, dec, w, h)):
        fields[p[0]] = (ra, dec, math.hypot(w, h) / 2)

def sep(a, b):
    ra1, d1, ra2, d2 = map(math.radians, (a[0], a[1], b[0], b[1]))
    c = math.sin(d1) * math.sin(d2) + math.cos(d1) * math.cos(d2) * math.cos(ra1 - ra2)
    return math.degrees(math.acos(max(-1.0, min(1.0, c))))

train, val, test = set(ids(train_path)), set(ids(val_path)), set(ids(test_path))
evals = set(i for p in eval_paths for i in ids(p))
eval_plates = {e: fields[slug(e)] for e in evals if slug(e) in fields}
missing_eval = sorted(e for e in evals if slug(e) not in fields)
if missing_eval:
    sys.exit(f"eval fields with no plate: {missing_eval}")

e16a_cells = sum(e16a_listed.get(s, 0) for s in train)
e16a_sessions = sum(1 for s in train if e16a_listed.get(s, 0) > 0)
print(f"E16a's sessions: {e16a_cells} bright cells over {e16a_sessions} sessions (their export sample left out)")
rows = []
for sid, n in sorted(counts.items(), key=lambda kv: -kv[1]):
    if n == 0:
        continue
    why = None
    if sid in train or sid in val:
        why = "E16a's own"
    elif "|flip=" in sid:
        why = "flip half"
    elif sid in test:
        why = "test split"
    elif sid in evals:
        why = "eval field"
    elif slug(sid) not in fields:
        why = "no position"
    else:
        plate = fields[slug(sid)]
        near = [e for e, ep in eval_plates.items() if sep(plate, ep) < plate[2] + ep[2]]
        if near:
            why = "overlaps " + near[0].split("|")[0].split("/")[2]
    rows.append((sid, n, why))

total, chosen = 0, []
for sid, n, why in rows:
    if why is None and (total + e16a_cells < NEED_CELLS or len(chosen) + e16a_sessions < NEED_SESSIONS):
        chosen.append(sid)
        total += min(PER_SESSION, n)
    print(f"{n:4d}  {'TAKEN' if sid in chosen else (why or 'not needed'):28s} {sid}")
print(f"\nwidened by {len(chosen)} sessions, {total} bright cells; with E16a's, {total + e16a_cells}")
with open("e16b-widen.txt", "w", encoding="utf-8") as f:
    f.write("# E16b's widened sessions (training/denoise/e16b_widen_select.py over the 2026-09-29-full bright-cell count):\n")
    f.write("# ranked by bright cells, E16a's own, flip halves, the test split, the eval fields and their overlapping plates out,\n")
    f.write("# taken until the arm holds 150 bright cells over three sessions.\n")
    for sid in chosen:
        f.write(sid + "\n")
