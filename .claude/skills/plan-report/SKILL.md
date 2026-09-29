---
name: plan-report
description: Report how docs/plans, the GitHub issues and the per-plan milestones agree (dead section links, issues outside their plan's milestone, stale plan statuses, plans without a summary row, milestones ready to close). Use when the user asks for a plan/backlog/tracking report, "what is tracked", "which plans are stale", or wants the milestones checked.
---

The backlog is GitHub issues. Each plan in `docs/plans/` names its issues section by section, and each issue
links its plan's section. Every plan with open work has a milestone of the same name holding its issues. The
link runs both ways, down to the paragraph: see "Project Tracking Docs" in CLAUDE.md.
`tools/plan-issue-report.py` checks all of that MECHANICALLY, from `gh` data and the plan files. No model reads
the plans, so running it costs almost nothing.

**Never fan out agents to do this survey.** The 2026-09-25 manual survey of 97 plans took ten agents and a large
part of a 5-hour usage window. The tool replaces it. A model is only needed afterwards, to read the few plans the
tool FLAGS.

## Where the report lives now: GitHub, not an artifact

Since 2026-09-28 the report is not published as an Artifact (a private page nobody else saw, refreshed only when
this skill ran, and one was lost outright). It lives in the repository:

- **The check** is the `plan-report` workflow (`.github/workflows/plan-report.yml`). On a PR that touches
  `docs/plans/` it runs the tool with `--strict-plans <the plans that PR changed>`: only an ERROR about one of
  them fails the PR, and an error elsewhere is listed without failing it (it is another piece of work's; #1065
  went red on a dead link in an issue another session had just opened). Weekly on Monday and on demand it only
  reports. The job summary lists every ERROR and WARN, and the HTML page is the run's `plan-report` artifact (a
  workflow upload, not a claude.ai Artifact).
- **What is happening** is the repository's shared issue views (https://github.com/SharpAstro/tianwen/issues/views,
  created in the web UI on 2026-09-28: up next, the bench queue, the triage inbox, open issues in no plan, recently
  closed; neither `gh` nor the API can create or list them), and per plan, the Milestones page.

## Run

Locally, from the repo root, when the user asks for the report or before a PR that edits plan headings:

```
python tools/plan-issue-report.py --json "<scratchpad>/plan-report.json" --markdown "<scratchpad>/plan-report.md"
```

The console output lists every ERROR and WARN; the Markdown is the same table the job summary shows, ready to
paste. Add `--html <path>` for the full page (plans, milestones, the issues linking no plan).

On GitHub, without a checkout: `gh workflow run plan-report.yml`, then `gh run list --workflow plan-report.yml`
and read the run's summary.

Do not publish the HTML as an Artifact unless the user asks for one.

## What the findings mean, and what to do

| Kind | Level | Meaning | Fix |
|---|---|---|---|
| `dead-anchor` | ERROR | An issue links `plan.md#anchor`, and no such heading exists. A heading was renamed, or the link was typed. Line permalinks (`#L12-L30`) are exempt. | Edit the issue's **Plan:** line to the current heading's anchor (the tool's `slug()` is GitHub's rule). A heading that carries a status or a date breaks its links whenever that changes: prefer keeping status out of headings. |
| `missing-plan` | ERROR | An issue links a plan file that does not exist. | Point it at the right plan, or drop the link. |
| `milestone-closed` | ERROR | A closed milestone still holds open issues. | Reopen the milestone, or move the issues. |
| `milestone` | WARN | An issue links plan X but sits in another milestone, or none. | `gh api -X PATCH repos/SharpAstro/tianwen/issues/<n> -F milestone=<X's number>`. An issue serving several plans keeps ONE home milestone, the plan on its **Plan:** line. |
| `no-milestone` | WARN | A plan has open issues and no milestone. | Create a milestone titled with the plan's stem, with the plan link in its description. |
| `no-summary-row` | WARN | A plan is missing from `docs/plans/summary.md`. | Add a row. |
| `stale-status` | WARN | A heuristic: the top status says NOT STARTED while a table row says DONE. | Read that one plan and correct its status line. |
| `milestone-done` | INFO | A milestone has no open issues left. | Check that the plan says DONE, then close the milestone. |
| `no-plan` | INFO | An open issue links no plan. Many are fine: small bugs, bench checks. | Link a plan only where one covers the issue. |

Report the counts and the ERROR/WARN list to the user. Fix mechanical findings (anchors, milestone moves) only
when asked, or when they are failing the workflow on a PR you own. Status corrections need the one flagged plan
read, which is a judgement call.
