---
name: plan-report
description: Report how docs/plans, the GitHub issues and the per-plan milestones agree (dead section links, issues outside their plan's milestone, stale plan statuses, plans without a summary row, milestones ready to close), and publish it as an artifact. Use when the user asks for a plan/backlog/tracking report, "what is tracked", "which plans are stale", or wants the milestones checked.
---

The backlog is GitHub issues. Each plan in `docs/plans/` names its issues section by section, and each issue
links its plan's section. Every plan with open work has a milestone of the same name holding its issues. The
link runs both ways, down to the paragraph: see "Project Tracking Docs" in CLAUDE.md.
`tools/plan-issue-report.py` checks all of that MECHANICALLY, from `gh` data and the plan files. No model reads
the plans, so running it costs almost nothing.

**Never fan out agents to do this survey.** The 2026-09-25 manual survey of 97 plans took ten agents and a large
part of a 5-hour usage window. The tool replaces it. A model is only needed afterwards, to read the few plans the
tool FLAGS.

## Run

From the repo root:

```
python tools/plan-issue-report.py --html "<scratchpad>/plan-report.html" --json "<scratchpad>/plan-report.json"
```

`--strict` exits 1 on any ERROR, for use in CI. The console output lists every ERROR and WARN.

## Publish

Publish the HTML with the Artifact tool:
- On the first run, pass `icon: "checklist"` and a one-line description.
- On later runs in the same session, pass the same `file_path`, so the page keeps its URL.
- If the user already has a "Plan Tracking Report" artifact from an earlier session, find it with
  `Artifact action: "list"` and republish to its `url` instead of creating a second one.

The page is self-contained: tokens for light and dark themes, IBM Plex Sans from Google Fonts, no scripts.

## What the findings mean, and what to do

| Kind | Level | Meaning | Fix |
|---|---|---|---|
| `dead-anchor` | ERROR | An issue links `plan.md#anchor`, and no such heading exists. A heading was renamed, or the link was typed. Line permalinks (`#L12-L30`) are exempt. | Edit the issue's **Plan:** line to the current heading's anchor (the tool's `slug()` is GitHub's rule). |
| `missing-plan` | ERROR | An issue links a plan file that does not exist. | Point it at the right plan, or drop the link. |
| `milestone-closed` | ERROR | A closed milestone still holds open issues. | Reopen the milestone, or move the issues. |
| `milestone` | WARN | An issue links plan X but sits in another milestone, or none. | `gh api -X PATCH repos/SharpAstro/tianwen/issues/<n> -F milestone=<X's number>`. An issue serving several plans keeps ONE home milestone, the plan on its **Plan:** line. |
| `no-milestone` | WARN | A plan has open issues and no milestone. | Create a milestone titled with the plan's stem, with the plan link in its description. |
| `no-summary-row` | WARN | A plan is missing from `docs/plans/summary.md`. | Add a row. |
| `stale-status` | WARN | A heuristic: the top status says NOT STARTED while a table row says DONE. | Read that one plan and correct its status line. |
| `milestone-done` | INFO | A milestone has no open issues left. | Check that the plan says DONE, then close the milestone. |
| `no-plan` | INFO | An open issue links no plan. Many are fine: small bugs, bench checks. | Link a plan only where one covers the issue. |

Report the counts and the ERROR/WARN list to the user. Fix mechanical findings (anchors, milestone moves) only
when asked. Status corrections need the one flagged plan read, which is a judgement call.
