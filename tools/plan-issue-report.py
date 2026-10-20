"""Plan/issue tracking report: how docs/plans, the GitHub issues and the plan milestones agree.

The backlog is GitHub issues; a plan keeps the design and names its issues section by section; an issue
links its plan's section; every plan with open work has a milestone of the same name holding its issues.
This checks all four mechanically (no model involved) and writes a JSON result, a self-contained HTML
page and a Markdown summary. Needs `gh` (authenticated) and Python 3.

    python tools/plan-issue-report.py --html out.html [--json out.json] [--markdown out.md]
                                      [--strict | --strict-plans PLAN ...]

--strict exits 1 when any ERROR-level finding exists. --strict-plans exits 1 only for an ERROR about one of
the named plans (their file stems): the plan-report workflow passes the plans a PR changed, so a heading
that PR renamed cannot leave an issue's link dead, while an error elsewhere in the repository (an issue
another piece of work opened) reports without failing someone else's PR. The weekly run fails nothing; the
issues and milestones are the shared view. --markdown is what that workflow writes to the job summary.

--roadmap writes the PUBLIC face of the same graph, https://sharpastro.github.io/tianwen/roadmap.html: what is next
(priority:high, the newest priority:next), the plans in progress and planned by area with their progress, the latest
releases and the shipped plans, and no findings. The pages workflow regenerates it on every deploy.
"""
import argparse
import html
import json
import os
import re
import subprocess
import sys
from datetime import datetime, timezone

REPO_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PLANS_DIR = os.path.join(REPO_DIR, "docs", "plans")
GH_REPO = "SharpAstro/tianwen"
BLOB = f"https://github.com/{GH_REPO}/blob/main/docs/plans/"
INDEX_PLANS = {"summary", "nina-parity", "pixinsight-parity", "astropy-parity"}


# ---------------------------------------------------------------- data

def gh(*args):
    out = subprocess.run(["gh", *args], capture_output=True, text=True, encoding="utf-8", cwd=REPO_DIR)
    if out.returncode != 0:
        sys.exit(f"gh {' '.join(args[:3])} failed: {out.stderr.strip()}")
    return json.loads(out.stdout)


def slug(heading):
    """GitHub's heading anchor: rendered text, lower-cased, punctuation dropped, spaces to hyphens."""
    h = re.sub(r"\[([^\]]*)\]\([^)]*\)", r"\1", heading).replace("`", "")
    h = re.sub(r"(?<!\w)[*_]{1,3}|[*_]{1,3}(?!\w)", "", h).strip().lower()
    return re.sub(r"[^\w\- ]", "", h).replace(" ", "-")


def read_plan(stem):
    text = open(os.path.join(PLANS_DIR, stem + ".md"), encoding="utf-8").read()
    anchors, seen, in_code = set(), {}, False
    for line in text.split("\n"):
        if line.startswith("```"):
            in_code = not in_code
            continue
        m = None if in_code else re.match(r"^(#{1,6})\s+(.*?)\s*#*\s*$", line)
        if m:
            base = slug(m.group(2))
            n = seen.get(base, 0)
            seen[base] = n + 1
            anchors.add(base if n == 0 else f"{base}-{n}")
    status = re.search(r"\*\*Status:?\s*([^*]+)\*\*", text)
    return {
        "text": text,
        "anchors": anchors,
        "named": sorted({int(n) for n in re.findall(r"(?<![\w/&])#(\d{2,5})\b", text)}),
        "status_line": status.group(1).strip() if status else None,
    }


def summary_rows():
    s = open(os.path.join(PLANS_DIR, "summary.md"), encoding="utf-8").read()
    return {m.group(1): m.group(2) for m in re.finditer(r"^\| \[([^\]]+)\]\([^)]*\) \| \*\*(.*?)\*\*", s, re.M)}


def plan_links(body):
    """[(plan, anchor or None)] in order; the **Plan:** line first."""
    body = body or ""
    found = re.findall(r"docs/plans/([A-Za-z0-9_.-]+?)\.md(?:#([A-Za-z0-9_\-]+))?", body)
    primary = re.findall(r"\*\*Plan:\*\*\s*\[[^\]]*\]\([^)]*docs/plans/([A-Za-z0-9_.-]+?)\.md", body)
    ordered = [(p, a or None) for p, a in found]
    ordered.sort(key=lambda x: 0 if x[0] in primary else 1)
    return ordered


# ---------------------------------------------------------------- checks

def build():
    stems = sorted(f[:-3] for f in os.listdir(PLANS_DIR) if f.endswith(".md"))
    plans = {s: read_plan(s) for s in stems}
    rows = summary_rows()
    issues = gh("issue", "list", "--repo", GH_REPO, "--state", "all", "--limit", "3000",
                "--json", "number,title,state,body,milestone,labels,url")
    by_num = {i["number"]: i for i in issues}
    # Paged: the repo passed 80 milestones on 2026-09-28, and one page holds 100.
    milestones = [m for page in gh("api", "--paginate", "--slurp", f"repos/{GH_REPO}/milestones?state=all&per_page=100")
                  for m in page]
    ms_by_title = {m["title"]: m for m in milestones}

    findings = []

    def add(level, kind, msg, plan=None, issue=None):
        findings.append({"level": level, "kind": kind, "msg": msg, "plan": plan, "issue": issue})

    per_plan = {s: {"open": [], "closed": [], "unlinked_named": []} for s in stems}
    for i in issues:
        links = plan_links(i["body"])
        homes = [p for p, _ in links if p in plans and p not in INDEX_PLANS]
        for p, a in links:
            if p not in plans:
                add("ERROR", "missing-plan", f"links docs/plans/{p}.md, which does not exist", issue=i["number"])
            elif a and not re.match(r"^L\d+(-L\d+)?$", a) and a not in plans[p]["anchors"]:  # #L12-L30 is a line permalink
                add("ERROR", "dead-anchor", f"links {p}.md#{a}, and that heading does not exist", plan=p, issue=i["number"])
        if homes:
            per_plan[homes[0]]["open" if i["state"] == "OPEN" else "closed"].append(i["number"])
        if i["state"] != "OPEN":
            continue
        ms = (i.get("milestone") or {}).get("title")
        if homes and ms != homes[0] and ms not in homes:
            add("WARN", "milestone", f"links {homes[0]} but is in milestone {ms or '(none)'}", plan=homes[0], issue=i["number"])
        if not links:
            add("INFO", "no-plan", "open issue that links no plan", issue=i["number"])

    for s, p in plans.items():
        if s == "summary":
            continue
        for n in p["named"]:
            i = by_num.get(n)
            if i is None:
                continue  # a PR number, most likely; issues and PRs share the sequence
            if s not in [q for q, _ in plan_links(i["body"])] and i["state"] == "OPEN":
                per_plan[s]["unlinked_named"].append(n)
        if s not in rows:
            add("WARN", "no-summary-row", "plan has no row in summary.md", plan=s)
        st = (p["status_line"] or "").upper()
        if st.startswith("NOT STARTED") and re.search(r"\|\s*\*{0,2}DONE", p["text"]):
            add("WARN", "stale-status", "top status says NOT STARTED, but a table row says DONE", plan=s)

    for title, m in ms_by_title.items():
        if title in plans:
            if m["state"] == "open" and m["open_issues"] == 0:
                add("INFO", "milestone-done", f"milestone has no open issues ({m['closed_issues']} closed): close it, and check the plan says DONE", plan=title)
            if m["state"] == "closed" and m["open_issues"] > 0:
                add("ERROR", "milestone-closed", f"closed milestone still holds {m['open_issues']} open issues", plan=title)
    for s in stems:
        if per_plan[s]["open"] and s not in ms_by_title and s not in INDEX_PLANS:
            add("WARN", "no-milestone", f"{len(per_plan[s]['open'])} open issues but no milestone", plan=s)

    table = []
    for s in stems:
        if s in INDEX_PLANS:
            continue
        m = ms_by_title.get(s)
        title = re.search(r"^#\s+(.+?)\s*$", plans[s]["text"], re.M)
        table.append({
            "plan": s,
            "title": re.sub(r"^Plan:\s*", "", title.group(1)) if title else s.replace("-", " ").capitalize(),
            "status_line": plans[s]["status_line"],
            "summary": rows.get(s),
            "milestone": {"number": m["number"], "open": m["open_issues"], "closed": m["closed_issues"],
                          "state": m["state"], "url": m["html_url"]} if m else None,
            "open": per_plan[s]["open"],
            "closed": per_plan[s]["closed"],
            "named_but_unlinked": per_plan[s]["unlinked_named"],
        })
    return {
        "generated": datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M UTC"),
        "counts": {
            "plans": len(table),
            "open_issues": sum(1 for i in issues if i["state"] == "OPEN"),
            "open_linked": sum(1 for i in issues if i["state"] == "OPEN" and plan_links(i["body"])),
            "milestones_open": sum(1 for m in milestones if m["state"] == "open"),
            **{lvl: sum(1 for f in findings if f["level"] == lvl) for lvl in ("ERROR", "WARN", "INFO")},
        },
        "plans": table,
        "findings": findings,
        "titles": {i["number"]: i["title"] for i in issues},
        "labels": {i["number"]: [l["name"] for l in i["labels"]] for i in issues if i["state"] == "OPEN"},
    }


# ---------------------------------------------------------------- html

CSS = """
:root{--bg:#f6f7f9;--panel:#ffffff;--ink:#1c2230;--dim:#5b6475;--rule:#dde1e8;--accent:#3b5bdb;
--ok:#2b8a3e;--warn:#b7791f;--err:#c92a2a;--bar:#e6e9ef;font-family:"IBM Plex Sans",system-ui,sans-serif}
@media (prefers-color-scheme:dark){:root:not([data-theme="light"]){--bg:#11141a;--panel:#181c24;--ink:#e3e7ef;
--dim:#98a1b3;--rule:#2a303c;--accent:#7c93f0;--ok:#51cf66;--warn:#fcc419;--err:#ff6b6b;--bar:#262c38;color-scheme:dark}}
:root[data-theme="dark"]{--bg:#11141a;--panel:#181c24;--ink:#e3e7ef;--dim:#98a1b3;--rule:#2a303c;--accent:#7c93f0;
--ok:#51cf66;--warn:#fcc419;--err:#ff6b6b;--bar:#262c38;color-scheme:dark}
body{background:var(--bg);color:var(--ink);margin:0;padding-block:24px;padding-inline:16px}
main{max-width:1100px;margin:0 auto;display:grid;gap:20px}
h1{font-size:1.5rem;margin:0}h2{font-size:1.1rem;margin:0 0 8px}
.sub{color:var(--dim);font-size:.9rem}
.stats{display:flex;flex-wrap:wrap;gap:10px}
.stat{background:var(--panel);border:1px solid var(--rule);border-radius:6px;padding:10px 14px;min-width:120px}
.stat b{display:block;font-size:1.4rem;font-variant-numeric:tabular-nums}
.stat span{color:var(--dim);font-size:.8rem;text-transform:uppercase;letter-spacing:.04em}
section{background:var(--panel);border:1px solid var(--rule);border-radius:6px;padding:14px}
.scroll{overflow-x:auto}
table{border-collapse:collapse;width:100%;font-size:.88rem}
th,td{text-align:left;padding:6px 8px;border-bottom:1px solid var(--rule);vertical-align:top}
th{color:var(--dim);font-weight:600;font-size:.78rem;text-transform:uppercase;letter-spacing:.04em}
td.num{font-variant-numeric:tabular-nums;white-space:nowrap}
.bar{height:8px;width:120px;background:var(--bar);border-radius:4px;overflow:hidden}
.bar i{display:block;height:100%;background:var(--ok)}
a{color:var(--accent);text-decoration:none}a:hover{text-decoration:underline}
.lvl{font-weight:700;font-size:.75rem}.ERROR{color:var(--err)}.WARN{color:var(--warn)}.INFO{color:var(--dim)}
.status{color:var(--dim);max-width:380px}
details summary{cursor:pointer;color:var(--dim)}
"""


def issue_link(n, titles):
    t = html.escape(titles.get(n, titles.get(str(n), "")))
    return f'<a href="https://github.com/{GH_REPO}/issues/{n}" title="{t}">#{n}</a>'


def render(r):
    titles = {int(k): v for k, v in r["titles"].items()}
    c = r["counts"]
    stats = "".join(f'<div class="stat"><b>{v}</b><span>{k}</span></div>' for k, v in [
        ("plans", c["plans"]), ("open issues", c["open_issues"]), ("linked to a plan", c["open_linked"]),
        ("milestones", c["milestones_open"]), ("errors", c["ERROR"]), ("warnings", c["WARN"])])
    rows = []
    for p in sorted(r["plans"], key=lambda x: (-(len(x["open"])), x["plan"])):
        if not p["open"] and not p["milestone"]:
            continue
        m = p["milestone"]
        total = (m["open"] + m["closed"]) if m else 0
        pct = int(100 * m["closed"] / total) if m and total else 0
        ms = (f'<a href="{m["url"]}">{m["closed"]}/{total}</a><div class="bar"><i style="width:{pct}%"></i></div>'
              if m else '<span class="WARN">none</span>')
        opens = " ".join(issue_link(n, titles) for n in p["open"][:12]) + (" ..." if len(p["open"]) > 12 else "")
        rows.append(f'<tr><td><a href="{BLOB}{p["plan"]}.md">{p["plan"]}</a></td><td class="num">{ms}</td>'
                    f'<td class="status">{html.escape(p["summary"] or "(no summary row)")}</td><td>{opens}</td></tr>')
    finds = []
    for f in sorted(r["findings"], key=lambda f: ("ERROR", "WARN", "INFO").index(f["level"])):
        if f["kind"] == "no-plan":
            continue
        where = (f'<a href="{BLOB}{f["plan"]}.md">{f["plan"]}</a> ' if f["plan"] else "") + (
            issue_link(f["issue"], titles) if f["issue"] else "")
        finds.append(f'<tr><td class="lvl {f["level"]}">{f["level"]}</td><td>{f["kind"]}</td><td>{where}</td>'
                     f'<td>{html.escape(f["msg"])}</td></tr>')
    labels = {int(k): v for k, v in r.get("labels", {}).items()}

    def area(n):
        return next((l[5:] for l in labels.get(n, []) if l.startswith("area:")), "")

    noplan = sorted((f["issue"] for f in r["findings"] if f["kind"] == "no-plan"), key=lambda n: (area(n) or "~", -n))
    noplan_rows = "".join(
        f'<tr><td class="num">{issue_link(n, titles)}</td><td>{html.escape(area(n) or "-")}</td>'
        f'<td>{html.escape(titles.get(n, ""))}</td>'
        f'<td class="sub">{html.escape(", ".join(l for l in labels.get(n, []) if not l.startswith("area:")))}</td></tr>'
        for n in noplan)
    return f"""<title>Plan Tracking Report</title>
<link rel="preconnect" href="https://fonts.googleapis.com"><link href="https://fonts.googleapis.com/css2?family=IBM+Plex+Sans:wght@400;600;700&display=swap" rel="stylesheet">
<style>{CSS}</style>
<main>
<header><h1>Plan tracking</h1><div class="sub">{GH_REPO}: docs/plans against issues and milestones. Generated {r["generated"]} by tools/plan-issue-report.py.</div></header>
<div class="stats">{stats}</div>
<section><h2>Findings ({c["ERROR"]} errors, {c["WARN"]} warnings)</h2><div class="scroll"><table>
<tr><th>Level</th><th>Kind</th><th>Where</th><th>What</th></tr>{"".join(finds) or '<tr><td colspan="4">None.</td></tr>'}</table></div></section>
<section><h2>Plans with open work</h2><div class="scroll"><table>
<tr><th>Plan</th><th>Milestone</th><th>Status (summary.md)</th><th>Open issues</th></tr>{"".join(rows)}</table></div></section>
<section><details><summary>{len(noplan)} open issues link no plan (by area)</summary><div class="scroll"><table>
<tr><th>Issue</th><th>Area</th><th>Title</th><th>Other labels</th></tr>{noplan_rows}</table></div></details></section>
</main>
"""


# ---------------------------------------------------------------- roadmap

ROADMAP_CSS = """
:root{--bg:#f6f7f9;--panel:#ffffff;--ink:#1c2230;--dim:#5b6475;--rule:#dde1e8;--accent:#3b5bdb;--bar:#e6e9ef;
--done:#2b8a3e;--prog:#3b5bdb;--plan:#868e96;--next:#c2410c;font-family:"IBM Plex Sans",system-ui,sans-serif;color-scheme:light}
@media (prefers-color-scheme:dark){:root:not([data-theme="light"]){--bg:#11141a;--panel:#181c24;--ink:#e3e7ef;--dim:#98a1b3;
--rule:#2a303c;--accent:#7c93f0;--bar:#262c38;--done:#51cf66;--prog:#7c93f0;--plan:#98a1b3;--next:#fb923c;color-scheme:dark}}
:root[data-theme="dark"]{--bg:#11141a;--panel:#181c24;--ink:#e3e7ef;--dim:#98a1b3;--rule:#2a303c;--accent:#7c93f0;
--bar:#262c38;--done:#51cf66;--prog:#7c93f0;--plan:#98a1b3;--next:#fb923c;color-scheme:dark}
*{box-sizing:border-box}
body{background:var(--bg);color:var(--ink);margin:0;padding:24px 16px 48px;line-height:1.45}
main{max-width:1180px;margin:0 auto;display:grid;gap:28px}
h1{font-size:1.7rem;margin:0}h2{font-size:1.2rem;margin:0 0 4px}h3{font-size:.8rem;margin:18px 0 8px;color:var(--dim);
text-transform:uppercase;letter-spacing:.06em}
.sub{color:var(--dim);font-size:.92rem;max-width:72ch}
a{color:var(--accent);text-decoration:none}a:hover{text-decoration:underline}
.stats{display:flex;flex-wrap:wrap;gap:10px}
.stat{background:var(--panel);border:1px solid var(--rule);border-radius:8px;padding:10px 14px;min-width:130px;flex:1 1 130px}
.stat b{display:block;font-size:1.5rem;font-variant-numeric:tabular-nums}
.stat span{color:var(--dim);font-size:.78rem;text-transform:uppercase;letter-spacing:.05em}
.grid{display:grid;gap:12px;grid-template-columns:repeat(auto-fill,minmax(min(100%,330px),1fr))}
.card{background:var(--panel);border:1px solid var(--rule);border-radius:8px;padding:14px;display:grid;gap:8px;align-content:start;min-width:0}
.card h4{margin:0;font-size:1rem;overflow-wrap:anywhere}
.badge{display:inline-block;font-size:.72rem;font-weight:600;padding:1px 8px;border-radius:999px;border:1px solid currentColor;
max-width:100%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;vertical-align:middle}
.prog{color:var(--prog)}.plan{color:var(--plan)}.done{color:var(--done)}.nx{color:var(--next)}
.meter{display:flex;align-items:center;gap:8px;font-size:.8rem;color:var(--dim);font-variant-numeric:tabular-nums}
.bar{flex:1;height:7px;background:var(--bar);border-radius:4px;overflow:hidden}.bar i{display:block;height:100%;background:var(--done)}
ul{margin:0;padding-left:18px;font-size:.88rem}li{margin:2px 0;overflow-wrap:anywhere}
.more{font-size:.82rem}
.next li{margin:6px 0}
.shipped{columns:3 260px;column-gap:24px;font-size:.88rem;padding-left:18px}
.release{background:var(--panel);border:1px solid var(--rule);border-radius:8px;padding:12px 14px;font-size:.9rem}
.release p{margin:6px 0 0;color:var(--dim)}
code{font-family:"IBM Plex Mono",ui-monospace,monospace;font-size:.86em}
footer{color:var(--dim);font-size:.82rem}
"""

# How many issues marked priority:next the roadmap lists, newest first; every priority:high one is listed.
NEXT_SHOWN = 8

AREA_NAMES = {"astrometry": "Astrometry", "drivers": "Devices and drivers", "guider": "Guiding", "imaging": "Imaging",
              "infra": "Infrastructure", "sequencing": "Sessions and sequencing", "ui": "Apps and UI", "archive": "Archive"}


def _inline_md(text, limit=None):
    """A changelog or status snippet as safe HTML: escaped, `code` kept, links and emphasis reduced to their text."""
    t = re.sub(r"\[([^\]]*)\]\([^)]*\)", r"\1", text)
    t = re.sub(r"\*\*([^*]+)\*\*|__([^_]+)__", lambda m: m.group(1) or m.group(2), t)
    if limit and len(t) > limit:
        t = t[:limit].rsplit(" ", 1)[0] + " ..."
    t = html.escape(t)
    return re.sub(r"`([^`]+)`", r"<code>\1</code>", t)


def _bucket(p):
    """Planned, in progress or shipped, read off the plan's own status and its milestone."""
    st = (p["summary"] or p["status_line"] or "").strip().upper()
    m = p["milestone"]
    open_n = (m["open"] if m else 0) or len(p["open"])
    closed_n = m["closed"] if m else len(p["closed"])
    if open_n == 0:
        return "shipped" if re.match(r"(DONE|SHIPPED|COMPLETE|RESOLVED|IMPLEMENTED)", st) else None
    if st.startswith("NOT STARTED") and closed_n == 0:
        return "planned"
    return "progress"


def _changelog(n=3):
    path = os.path.join(REPO_DIR, "CHANGELOG.md")
    if not os.path.exists(path):
        return []
    text = open(path, encoding="utf-8").read()
    out = []
    for m in re.finditer(r"^## (\S+)\s*\n(.*?)(?=^## |\Z)", text, re.M | re.S):
        para = next((b.strip() for b in m.group(2).split("\n\n") if b.strip()), "")
        out.append((m.group(1), " ".join(para.split())))
        if len(out) == n:
            break
    return out


def render_roadmap(r):
    """The public face of the same graph: what is next, what is in progress, what is planned and what shipped, by area.
    No findings: those are the maintainers' checklist, and this page is for everyone else."""
    titles = {int(k): v for k, v in r["titles"].items()}
    labels = {int(k): v for k, v in r.get("labels", {}).items()}

    def area_of(nums):
        votes = {}
        for n in nums:
            for l in labels.get(n, []):
                if l.startswith("area:"):
                    votes[l[5:]] = votes.get(l[5:], 0) + 1
        return max(votes, key=votes.get) if votes else "other"

    def issue_li(n):
        return (f'<li><a href="https://github.com/{GH_REPO}/issues/{n}">#{n}</a> '
                f'{_inline_md(titles.get(n, ""), 110)}</li>')

    buckets = {"progress": [], "planned": [], "shipped": []}
    plan_of = {}
    for p in r["plans"]:
        b = _bucket(p)
        if b:
            buckets[b].append(p)
        for n in p["open"]:
            plan_of[n] = p

    def card(p, kind):
        m = p["milestone"]
        total = (m["open"] + m["closed"]) if m else len(p["open"]) + len(p["closed"])
        closed = m["closed"] if m else len(p["closed"])
        pct = int(round(100 * closed / total)) if total else 0
        status = _inline_md(p["summary"] or p["status_line"] or ("planned" if kind == "planned" else "in progress"), 60)
        badge = {"progress": "prog", "planned": "plan"}[kind]
        opens = sorted(p["open"], key=lambda n: (not any(l.startswith("priority:") for l in labels.get(n, [])), -n))
        more = len(opens) - 4
        tail = (f'<div class="more"><a href="{m["url"]}">and {more} more</a></div>' if m and more > 0
                else (f'<div class="more">and {more} more</div>' if more > 0 else ""))
        meter = (f'<div class="meter"><div class="bar"><i style="width:{pct}%"></i></div>{closed} of {total} done</div>'
                 if total else "")
        return (f'<article class="card"><h4><a href="{BLOB}{p["plan"]}.md">{_inline_md(p["title"], 90)}</a></h4>'
                f'<div><span class="badge {badge}" title="{html.escape(p["summary"] or "")}">{status}</span></div>{meter}'
                f'<ul>{"".join(issue_li(n) for n in opens[:4])}</ul>{tail}</article>')

    def by_area(plans, kind):
        groups = {}
        for p in plans:
            groups.setdefault(area_of(p["open"]), []).append(p)
        out = []
        for a in sorted(groups, key=lambda a: (a == "other", AREA_NAMES.get(a, a))):
            ps = sorted(groups[a], key=lambda p: (-(p["milestone"]["closed"] if p["milestone"] else 0), p["plan"]))
            out.append(f'<h3>{html.escape(AREA_NAMES.get(a, "Other"))}</h3>'
                       f'<div class="grid">{"".join(card(p, kind) for p in ps)}</div>')
        return "".join(out)

    high = sorted((n for n, ls in labels.items() if "priority:high" in ls), key=lambda n: -n)
    nexts = sorted((n for n, ls in labels.items() if "priority:next" in ls and "priority:high" not in ls), key=lambda n: -n)

    def plan_note(n):
        # The plan's name, unless the issue's own title already says it.
        if n not in plan_of:
            return ""
        t, it = plan_of[n]["title"].lower(), titles.get(n, "").lower()
        return "" if t.startswith(it[:40]) or it.startswith(t[:40]) else f' <span class="sub">({_inline_md(plan_of[n]["title"], 60)})</span>'

    next_items = "".join(
        f'<li><span class="badge nx">{"high" if n in high else "next"}</span> '
        f'<a href="https://github.com/{GH_REPO}/issues/{n}">#{n}</a> {_inline_md(titles.get(n, ""), 130)}{plan_note(n)}</li>'
        for n in high + nexts[:NEXT_SHOWN])
    if len(nexts) > NEXT_SHOWN:
        next_items += (f'<li class="more"><a href="https://github.com/{GH_REPO}/issues?q=is%3Aissue+is%3Aopen+label%3Apriority%3Anext">'
                       f'and {len(nexts) - NEXT_SHOWN} more marked next</a></li>')
    shipped = "".join(f'<li><a href="{BLOB}{p["plan"]}.md">{_inline_md(p["title"], 80)}</a></li>'
                      for p in sorted(buckets["shipped"], key=lambda p: p["title"].lower()))
    releases = "".join(f'<div class="release"><b>{html.escape(v)}</b><p>{_inline_md(t, 320)}</p></div>'
                       for v, t in _changelog())
    open_total = sum(len(p["open"]) for p in buckets["progress"] + buckets["planned"])
    done_total = sum((p["milestone"]["closed"] if p["milestone"] else 0) for p in r["plans"])
    stats = "".join(f'<div class="stat"><b>{v}</b><span>{k}</span></div>' for k, v in [
        ("in progress", len(buckets["progress"])), ("planned", len(buckets["planned"])),
        ("shipped plans", len(buckets["shipped"])), ("open items", open_total), ("items done", done_total)])
    return f"""<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Tianwen Roadmap</title>
<meta name="description" content="What Tianwen is working on, what is planned and what has shipped, generated from its plans and milestones.">
<link rel="preconnect" href="https://fonts.googleapis.com"><link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link href="https://fonts.googleapis.com/css2?family=IBM+Plex+Mono&family=IBM+Plex+Sans:wght@400;600;700&display=swap" rel="stylesheet">
<style>{ROADMAP_CSS}</style></head>
<body><main>
<header><h1>Tianwen roadmap</h1>
<p class="sub">What is being built, what is planned and what has shipped, generated from the repository's
<a href="https://github.com/{GH_REPO}/tree/main/docs/plans">plans</a> and
<a href="https://github.com/{GH_REPO}/milestones">milestones</a> on {html.escape(r["generated"])}.
A plan is a design and the reasoning behind it, not a promise of a date. Released versions are on the
<a href="https://github.com/{GH_REPO}/releases">releases page</a>.</p></header>
<div class="stats">{stats}</div>
{f'<section><h2>Next up</h2><ul class="next">{next_items}</ul></section>' if next_items else ""}
<section><h2>In progress</h2><p class="sub">Plans with work done and work open, by area; most done first.</p>{by_area(buckets["progress"], "progress")}</section>
<section><h2>Planned</h2><p class="sub">Designed and tracked, not started.</p>{by_area(buckets["planned"], "planned")}</section>
<section><h2>Latest releases</h2><div class="grid">{releases}</div>
<p class="sub"><a href="https://github.com/{GH_REPO}/blob/main/CHANGELOG.md">The full changelog</a></p></section>
<section><h2>Shipped plans</h2><ul class="shipped">{shipped}</ul></section>
<footer>Generated by <code>tools/plan-issue-report.py --roadmap</code>, from <a href="https://github.com/{GH_REPO}">{GH_REPO}</a>.</footer>
</main></body></html>
"""


def render_markdown(r):
    """The counts and every ERROR and WARN, as the CI job summary shows them."""
    c = r["counts"]
    out = [f"### Plan tracking: {c['ERROR']} errors, {c['WARN']} warnings",
           "",
           f"{c['plans']} plans, {c['open_issues']} open issues ({c['open_linked']} link a plan), "
           f"{c['milestones_open']} open milestones.",
           ""]
    shown = [f for f in r["findings"] if f["level"] != "INFO"]
    if shown:
        out += ["| Level | Kind | Plan | Issue | Finding |", "|---|---|---|---|---|"]
        for f in sorted(shown, key=lambda f: ("ERROR", "WARN").index(f["level"])):
            issue = f"#{f['issue']}" if f["issue"] else ""
            msg = f["msg"].replace("|", "\\|")
            out.append(f"| {f['level']} | {f['kind']} | {f['plan'] or ''} | {issue} | {msg} |")
    else:
        out.append("Nothing to fix.")
    out += ["", "What each kind means and how to fix it: `.claude/skills/plan-report/SKILL.md`."]
    return "\n".join(out) + "\n"


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--html")
    ap.add_argument("--json")
    ap.add_argument("--markdown")
    ap.add_argument("--roadmap", help="write the public roadmap page (no findings) to this path")
    ap.add_argument("--strict", action="store_true")
    ap.add_argument("--strict-plans", nargs="*", metavar="PLAN",
                    help="exit 1 only for an ERROR about one of these plans (file stems, e.g. denoiser-training)")
    a = ap.parse_args()
    r = build()
    if a.json:
        json.dump(r, open(a.json, "w", encoding="utf-8"), indent=1)
    if a.html:
        open(a.html, "w", encoding="utf-8", newline="\n").write(render(r))
    if a.markdown:
        open(a.markdown, "w", encoding="utf-8", newline="\n").write(render_markdown(r))
    if a.roadmap:
        open(a.roadmap, "w", encoding="utf-8", newline="\n").write(render_roadmap(r))
    c = r["counts"]
    print(f"{c['plans']} plans, {c['open_issues']} open issues ({c['open_linked']} linked), "
          f"{c['milestones_open']} milestones; {c['ERROR']} errors, {c['WARN']} warnings, {c['INFO']} info")
    for f in r["findings"]:
        if f["level"] != "INFO":
            print(f"  {f['level']:5s} {f['kind']:15s} {f['plan'] or ''} {('#' + str(f['issue'])) if f['issue'] else ''} {f['msg']}")
    if a.strict:
        sys.exit(1 if c["ERROR"] else 0)
    if a.strict_plans is not None:
        scope = set(a.strict_plans)
        own = [f for f in r["findings"] if f["level"] == "ERROR" and f["plan"] in scope]
        others = c["ERROR"] - len(own)
        print(f"strict for {sorted(scope) or 'no plan'}: {len(own)} error(s) there"
              + (f"; {others} elsewhere, reported and not failed" if others else ""))
        sys.exit(1 if own else 0)
    sys.exit(0)


if __name__ == "__main__":
    main()
